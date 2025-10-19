using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using Terminal.Gui;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using StuiPodcast.App;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using System.Text;
using System.Runtime.InteropServices;
using ThemeMode = StuiPodcast.App.UI.Shell.ThemeMode;
using StuiPodcast.Infra.Player;
using StuiPodcast.Infra.Opml;

class Program
{
    internal static bool SkipSaveOnExit = false;
    
    // ---------- CLI parsed options ----------
    sealed class CliOptions
    {
        public string? Engine;                 // --engine
        public string? Theme;                  // --theme
        public string? Feed;                   // --feed
        public string? Search;                 // --search
        public string? OpmlImport;             // --opml-import
        public string? OpmlImportMode;         // --import-mode (merge|replace|dry-run)
        public string? OpmlExport;             // --opml-export
        public bool Offline;                   // --offline
        public bool Ascii;                     // --ascii
        public string? LogLevel;               // --log-level
        public bool ShowVersion;               // --version|-v|-V
        public bool ShowHelp;                  // --help|-h|-?
    }

    static CliOptions ParseArgs(string[]? args)
    {
        var o = new CliOptions();
        if (args == null || args.Length == 0) return o;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];

            switch (a)
            {
                case "--version":
                case "-v":
                case "-V":
                    o.ShowVersion = true; break;

                case "--help":
                case "-h":
                case "-?":
                    o.ShowHelp = true; break;

                case "--engine":
                    if (i + 1 < args.Length) o.Engine = args[++i].Trim().ToLowerInvariant();
                    break;

                case "--theme":
                    if (i + 1 < args.Length) o.Theme = args[++i].Trim().ToLowerInvariant();
                    break;

                case "--feed":
                    if (i + 1 < args.Length) o.Feed = args[++i].Trim();
                    break;

                case "--search":
                    if (i + 1 < args.Length) o.Search = args[++i];
                    break;

                case "--opml-import":
                    if (i + 1 < args.Length) o.OpmlImport = args[++i];
                    break;

                case "--import-mode":
                    if (i + 1 < args.Length) o.OpmlImportMode = args[++i].Trim().ToLowerInvariant();
                    break;

                case "--opml-export":
                    if (i + 1 < args.Length) o.OpmlExport = args[++i];
                    break;

                case "--offline":
                    o.Offline = true; break;

                case "--ascii":
                    o.Ascii = true; break;

                case "--log-level":
                    if (i + 1 < args.Length) o.LogLevel = args[++i].Trim().ToLowerInvariant();
                    break;
            }
        }
        return o;
    }

    // SaveAsync-Throttle (klassenweite States)
    static readonly object _saveGate = new();
    static DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    static bool _savePending = false;

    static bool _saveRunning = false;
    static object? _netTimerToken;

    static readonly System.Collections.Generic.Dictionary<Guid, DownloadState> _dlLast = new();
    static DateTime _dlLastUiPulse = DateTime.MinValue;

    static volatile bool _netProbeRunning = false;
    static int _netConsecOk = 0, _netConsecFail = 0;
    const int FAILS_FOR_OFFLINE = 4;
    const int SUCC_FOR_ONLINE   = 3;
    static DateTimeOffset _netLastFlip = DateTimeOffset.MinValue;
    static readonly TimeSpan _netMinDwell = TimeSpan.FromSeconds(15);

    static readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

    static void LogNicsSnapshot()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ip = nic.GetIPProperties();
                var ipv4 = string.Join(",", ip.UnicastAddresses
                                         .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                         .Select(a => a.Address.ToString()));
                var gw   = string.Join(",", ip.GatewayAddresses.Select(g => g.Address?.ToString() ?? ""));
                var dns  = string.Join(",", ip.DnsAddresses.Select(d => d.ToString()));
                Log.Information("net/nic name={Name} type={Type} op={Op} spd={Speed} ipv4=[{IPv4}] gw=[{Gw}] dns=[{Dns}]",
                    nic.Name, nic.NetworkInterfaceType, nic.OperationalStatus, nic.Speed, ipv4, gw, dns);
            }
        } catch (Exception ex) { Log.Debug(ex, "net/nic snapshot failed"); }
    }

    static async Task<bool> TcpCheckAsync(string hostOrIp, int port, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var sock = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            var start = DateTime.UtcNow;
            var task = sock.ConnectAsync(hostOrIp, port, cts.Token).AsTask();
            await task.ConfigureAwait(false);
            var ms = (int)(DateTime.UtcNow - start).TotalMilliseconds;
            Log.Debug("net/probe tcp ok {Host}:{Port} in {Ms}ms", hostOrIp, port, ms);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "net/probe tcp fail {Host}:{Port}", hostOrIp, port);
            return false;
        }
    }

    static async Task<bool> HttpProbeAsync(string url)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _probeHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            sw.Stop();
            Log.Debug("net/probe http {Url} {Code} in {Ms}ms", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);
            return ((int)resp.StatusCode) is >= 200 and < 400;
        }
        catch (Exception exHead)
        {
            try
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await _probeHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                sw2.Stop();
                Log.Debug("net/probe http[GET] {Url} {Code} in {Ms}ms (HEAD failed: {Err})",
                    url, (int)resp.StatusCode, sw2.ElapsedMilliseconds, exHead.GetType().Name);
                return ((int)resp.StatusCode) is >= 200 and < 400;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "net/probe http fail {Url}", url);
                return false;
            }
        }
    }

    static async Task<bool> QuickNetCheckAsync()
    {
        var swAll = System.Diagnostics.Stopwatch.StartNew();

        bool anyUp = false;
        try
        {
            anyUp = NetworkInterface.GetIsNetworkAvailable();
            Log.Debug("net/probe nics-available={Avail}", anyUp);
        } catch { }

        var tcpOk =
            await TcpCheckAsync("1.1.1.1", 443, 900).ConfigureAwait(false) ||
            await TcpCheckAsync("8.8.8.8", 53, 900).ConfigureAwait(false);

        var httpOk =
            await HttpProbeAsync("http://connectivitycheck.gstatic.com/generate_204").ConfigureAwait(false) ||
            await HttpProbeAsync("http://www.msftconnecttest.com/connecttest.txt").ConfigureAwait(false);

        swAll.Stop();
        Log.Debug("net/probe result tcp={TcpOk} http={HttpOk} anyNicUp={NicUp} total={Ms}ms",
            tcpOk, httpOk, anyUp, swAll.ElapsedMilliseconds);

        var online = (tcpOk || httpOk) && anyUp;
        return online;
    }

    static void OnNetworkChanged(bool online)
    {
        var prev = Data.NetworkOnline;
        Data.NetworkOnline = online;

        Application.MainLoop?.Invoke(() =>
        {
            if (UI == null) return;

            CommandRouter.ApplyList(UI, Data);
            UI.RefreshEpisodesForSelectedFeed(Data.Episodes);

            var nowId = UI.GetNowPlayingId();
            if (nowId != null)
            {
                var ep = Data.Episodes.FirstOrDefault(x => x.Id == nowId);
                if (ep != null)
                    UI.SetWindowTitle((!Data.NetworkOnline ? "[OFFLINE] " : "") + (ep.Title ?? "—"));
            }

            if (!online) UI.ShowOsd("net: offline", 800);
        });

        _ = SaveAsync();
    }

    static AppData Data = new();
    static FeedService? Feeds;

    static SwappablePlayer? Player;
    static string? _initialEngineInfo;

    static Shell? UI;
    static PlaybackCoordinator? Playback;
    static MemoryLogSink MemLog = new(2000);

    static object? _uiTimer;
    static int _exitOnce = 0;

    static DownloadManager? Downloader;

    static async Task Main(string[]? args)
    {
        var cli = ParseArgs(args);

        if (cli.ShowVersion)
        {
            PrintVersion();
            return;
        }
        if (cli.ShowHelp)
        {
            PrintHelp();
            return;
        }

        // --- Windows-Konsole für Unicode/ANSI fit machen ---
        EnableWindowsConsoleAnsi();

        // ASCII-only Glyphs (falls gewünscht) – vor Application.Init
        if (cli.Ascii)
        {
            try { GlyphSet.Use(GlyphSet.Profile.Ascii); } catch { /* best effort */ }
        }

        ConfigureLogging(cli.LogLevel);
        InstallGlobalErrorHandlers();

        Data  = await AppStorage.LoadAsync();
        Feeds = new FeedService(Data);

        // CLI: preferred engine vor Player-Erzeugung übernehmen
        if (!string.IsNullOrWhiteSpace(cli.Engine))
            Data.PreferredEngine = cli.Engine!.Trim().ToLowerInvariant();

        _ = Task.Run(async () =>
        {
            bool online = await QuickNetCheckAsync();
            _netConsecOk   = online ? 1 : 0;
            _netConsecFail = online ? 0 : 1;
            _netLastFlip   = DateTimeOffset.UtcNow;
            OnNetworkChanged(online);
        });

        // 1) Core-Engine erzeugen …
        try
        {
            var core = PlayerFactory.Create(Data, out var engineInfo);
            Player = new SwappablePlayer(core);
            _initialEngineInfo = engineInfo;
        }
        catch (Exception)
        {
            UI?.ShowOsd("No audio engine found", 2000);
            throw;
        }

        // 3) Coordinator
        Playback = new PlaybackCoordinator(Data, Player, SaveAsync, MemLog);
        Downloader = new DownloadManager(Data);

        // Restore Player prefs
        try
        {
            var v = Math.Clamp(Data.Volume0_100, 0, 100);
            if (v != 0 || Data.Volume0_100 == 0) Player.SetVolume(v);
            var s = Data.Speed;
            if (s <= 0) s = 1.0;
            Player.SetSpeed(Math.Clamp(s, 0.25, 3.0));
        } catch { }

        Console.TreatControlCAsInput = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; QuitApp(); };
        AppDomain.CurrentDomain.ProcessExit += (_, __) => TerminalUtil.ResetHard();

        Application.Init();

        UI = new Shell(MemLog);

        // Startfeed („All“) als initiale Selektion
        try { Data.LastSelectedFeedId = UI.AllFeedId; } catch { }

        UI.Build();

        // Theme per CLI?
        if (!string.IsNullOrWhiteSpace(cli.Theme))
        {
            var t = cli.Theme!.Trim().ToLowerInvariant();
            ThemeMode tm = t switch
            {
                "base"   => ThemeMode.Base,
                "accent" => ThemeMode.MenuAccent,
                "native" => ThemeMode.Native,
                "auto"   => (OperatingSystem.IsWindows() ? ThemeMode.Base : ThemeMode.MenuAccent),
                _        => (OperatingSystem.IsWindows() ? ThemeMode.Base : ThemeMode.MenuAccent)
            };
            try { UI.SetTheme(tm); Data.ThemePref = tm.ToString(); _ = SaveAsync(); } catch { }
        }
        else
        {
            // ansonsten: gespeicherten/Default anwenden
            ThemeMode desired;
            if (!string.IsNullOrWhiteSpace(Data.ThemePref) &&
                Enum.TryParse<ThemeMode>(Data.ThemePref, out var saved))
                desired = saved;
            else
                desired = OperatingSystem.IsWindows() ? ThemeMode.Base : ThemeMode.MenuAccent;

            UI.SetTheme(desired);
        }

        // Scroll die Feeds-Liste nach oben & selektiere sichtbaren Start
        Application.MainLoop?.AddIdle(() =>
        {
            try { UI.EnsureSelectedFeedVisibleAndTop(); } catch { }
            return false;
        });

        // Episoden initial auf "All" + ganz nach oben
        Application.MainLoop?.AddIdle(() =>
        {
            try
            {
                UI.SetEpisodesForFeed(UI.AllFeedId, Data.Episodes);
                Application.MainLoop!.AddIdle(() =>
                {
                    try { UI.ScrollEpisodesToTopAndFocus(); } catch { }
                    return false;
                });
            } catch { }
            return false;
        });

        // Theme persistieren, wenn im UI geändert
        UI.ThemeChanged += mode =>
        {
            Data.ThemePref = mode.ToString();
            _ = SaveAsync();
        };

        // Netzwerkperiodik
        _ = Task.Run(async () =>
        {
            var online = await QuickNetCheckAsync();
            OnNetworkChanged(online);
        });
        try
        {
            NetworkChange.NetworkAvailabilityChanged += (s, e) => { TriggerNetProbe(); };
        } catch { }
        _netTimerToken = Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(3), _ =>
        {
            TriggerNetProbe();
            return true;
        });

        // Lookups
        UI.SetQueueLookup(id => Data.Queue.Contains(id));
        UI.SetDownloadStateLookup(id => Data.DownloadMap.TryGetValue(id, out var s) ? s.State : DownloadState.None);
        UI.SetOfflineLookup(() => !Data.NetworkOnline);

        // Downloader → UI
        Downloader.StatusChanged += (id, st) =>
        {
            var prev = _dlLast.TryGetValue(id, out var p) ? p : DownloadState.None;
            _dlLast[id] = st.State;

            if (prev != st.State)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    UI?.RefreshEpisodesForSelectedFeed(Data.Episodes);
                    if (UI != null)
                    {
                        switch (st.State)
                        {
                            case DownloadState.Queued:     UI.ShowOsd("⌵", 300); break;
                            case DownloadState.Running:    UI.ShowOsd("⇣", 300); break;
                            case DownloadState.Verifying:  UI.ShowOsd("≈", 300); break;
                            case DownloadState.Done:       UI.ShowOsd("⬇", 500); break;
                            case DownloadState.Failed:     UI.ShowOsd("!", 900);  break;
                            case DownloadState.Canceled:   UI.ShowOsd("×", 400);  break;
                        }
                    }
                });
                _ = SaveAsync();
                return;
            }

            if (st.State == DownloadState.Running && (DateTime.UtcNow - _dlLastUiPulse) > TimeSpan.FromMilliseconds(500))
            {
                _dlLastUiPulse = DateTime.UtcNow;
                Application.MainLoop?.Invoke(() => UI?.RefreshEpisodesForSelectedFeed(Data.Episodes));
            }
        };

        Downloader.EnsureRunning();

        UI.EpisodeSorter = eps => ApplySort(eps, Data);
        UI.SetHistoryLimit(Data.HistorySize);

        UI.SetPlayerPlacement(Data.PlayerAtTop);
        UI.SetUnplayedFilterVisual(Data.UnplayedOnly);

        // --- wire shell events ---
        UI.QuitRequested += () => QuitApp();

        UI.AddFeedRequested += async url =>
        {
            var f = await Feeds!.AddFeedAsync(url);

            Data.LastSelectedFeedId = f.Id;
            Data.LastSelectedEpisodeIndexByFeed[f.Id] = 0;
            _ = SaveAsync();

            UI.SetFeeds(Data.Feeds, f.Id);
            UI.SetEpisodesForFeed(f.Id, Data.Episodes);
            UI.SelectEpisodeIndex(0);
        };

        UI.RefreshRequested += async () =>
        {
            await Feeds!.RefreshAllAsync();

            var selected = UI.GetSelectedFeedId() ?? Data.LastSelectedFeedId;
            UI.SetFeeds(Data.Feeds, selected);

            if (selected != null)
            {
                UI.SetEpisodesForFeed(selected.Value, Data.Episodes);

                if (Data.LastSelectedEpisodeIndexByFeed.TryGetValue(selected.Value, out var idx))
                    UI.SelectEpisodeIndex(idx);
            }
            CommandRouter.ApplyList(UI, Data);
        };

        UI.SelectedFeedChanged += () =>
        {
            var fid = UI.GetSelectedFeedId();
            Data.LastSelectedFeedId = fid;

            if (fid != null)
            {
                int idx = 0;
                if (!Data.LastSelectedEpisodeIndexByFeed.TryGetValue(fid.Value, out idx))
                {
                    if (Data.LastSelectedEpisodeIndex is int legacy) idx = legacy;
                }

                UI.SetEpisodesForFeed(fid.Value, Data.Episodes);
                UI.SelectEpisodeIndex(idx);
            }

            _ = SaveAsync();
        };

        UI.EpisodeSelectionChanged += () =>
        {
            var fid = UI.GetSelectedFeedId();
            if (fid != null)
            {
                Data.LastSelectedEpisodeIndexByFeed[fid.Value] = UI.GetSelectedEpisodeIndex();
                _ = SaveAsync();
            }
        };

        UI.PlaySelected += () =>
        {
            var ep = UI.GetSelectedEpisode();
            if (ep == null || Player == null || Playback == null || UI == null) return;

            var curFeed = UI.GetSelectedFeedId();

            ep.LastPlayedAt = DateTimeOffset.Now;
            _ = SaveAsync();

            if (curFeed is Guid fid && fid == UI.QueueFeedId)
            {
                int ix = Data.Queue.FindIndex(id => id == ep.Id);
                if (ix >= 0)
                {
                    Data.Queue.RemoveRange(0, ix + 1);
                    UI.SetQueueOrder(Data.Queue);
                    UI.RefreshEpisodesForSelectedFeed(Data.Episodes);
                    _ = SaveAsync();
                }
            }

            string? localPath = null;
            if (Data.DownloadMap.TryGetValue(ep.Id, out var st)
                && st.State == DownloadState.Done
                && !string.IsNullOrWhiteSpace(st.LocalPath)
                && File.Exists(st.LocalPath))
            {
                localPath = st.LocalPath;
            }

            bool isRemote =
                string.IsNullOrWhiteSpace(localPath) &&
                !string.IsNullOrWhiteSpace(ep.AudioUrl) &&
                ep.AudioUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

            var baseline = TimeSpan.Zero;
            try { baseline = Player?.State.Position ?? TimeSpan.Zero; } catch { }
            UI.SetPlayerLoading(true, isRemote ? "Loading…" : "Opening…", baseline);

            var mode   = (Data.PlaySource ?? "auto").Trim().ToLowerInvariant();
            var online = Data.NetworkOnline;

            string? source = mode switch
            {
                "local"  => localPath,
                "remote" => ep.AudioUrl,
                _        => localPath ?? (online ? ep.AudioUrl : null)
            };

            if (string.IsNullOrWhiteSpace(source))
            {
                UI.SetPlayerLoading(false);
                var msg = (localPath == null)
                    ? "∅ Offline: not downloaded"
                    : "No playable source";
                UI.ShowOsd(msg, 1500);
                return;
            }

            var oldUrl = ep.AudioUrl;
            try
            {
                ep.AudioUrl = source;
                Playback.Play(ep);
            }
            catch
            {
                UI.SetPlayerLoading(false);
                throw;
            }
            finally
            {
                ep.AudioUrl = oldUrl;
            }

            UI.SetWindowTitle((!Data.NetworkOnline ? "[OFFLINE] " : "") + ep.Title);
            UI.SetNowPlaying(ep.Id);

            if (localPath != null)
            {
                Application.MainLoop?.AddTimeout(TimeSpan.FromMilliseconds(600), _ =>
                {
                    try
                    {
                        var s = Player.State;
                        if (!s.IsPlaying && s.Position == TimeSpan.Zero)
                        {
                            try { Player.Stop(); } catch { }

                            var fileUri = new Uri(localPath).AbsoluteUri;

                            var old = ep.AudioUrl;
                            try
                            {
                                ep.AudioUrl = fileUri;
                                Playback.Play(ep);
                                UI.ShowOsd("Retry (file://)");
                            }
                            finally { ep.AudioUrl = old; }
                        }
                    }
                    catch { }
                    return false;
                });
            }
        };

        UI.ToggleThemeRequested += () => UI.ToggleTheme();

        UI.TogglePlayedRequested += () =>
        {
            var ep = UI?.GetSelectedEpisode();
            if (ep == null) return;

            ep.Played = !ep.Played;

            if (ep.Played)
            {
                if (ep.LengthMs is long len) ep.LastPosMs = len;
                ep.LastPlayedAt = DateTimeOffset.Now;
            }
            else
            {
                ep.LastPosMs = 0;
            }

            _ = SaveAsync();

            var fid = UI.GetSelectedFeedId();
            if (fid != null) UI.SetEpisodesForFeed(fid.Value, Data.Episodes);
            UI.ShowDetails(ep);
        };

        UI.Command += cmd =>
        {
            if (UI == null || Player == null || Playback == null || Downloader == null) return;

            if (CommandRouter.HandleQueue(cmd, UI, Data, SaveAsync))
            {
                UI.SetQueueOrder(Data.Queue);
                UI.RefreshEpisodesForSelectedFeed(Data.Episodes);
                return;
            }

            if (CommandRouter.HandleDownloads(cmd, UI, Data, Downloader, SaveAsync))
                return;

            CommandRouter.Handle(cmd, Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
        };

        UI.SearchApplied += query =>
        {
            var fid = UI?.GetSelectedFeedId();
            var list = Data.Episodes.AsEnumerable();
            if (fid != null) list = list.Where(e => e.FeedId == fid.Value);

            if (!string.IsNullOrWhiteSpace(query))
                list = list.Where(e =>
                    (e.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

            if (fid != null) UI?.SetEpisodesForFeed(fid.Value, list);
        };

        // --- initial lists ---
        UI.SetFeeds(Data.Feeds, Data.LastSelectedFeedId);
        UI.SetUnplayedHint(Data.UnplayedOnly);
        CommandRouter.ApplyList(UI, Data);

        var initialFeed = UI.GetSelectedFeedId();
        if (initialFeed != null)
        {
            int idx = 0;
            if (!Data.LastSelectedEpisodeIndexByFeed.TryGetValue(initialFeed.Value, out idx))
            {
                if (Data.LastSelectedEpisodeIndex is int legacy) idx = legacy;
            }

            UI.SetEpisodesForFeed(initialFeed.Value, Data.Episodes);
            UI.SelectEpisodeIndex(idx);
        }

        var last = Data.Episodes
            .OrderByDescending(e => e.LastPlayedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.LastPosMs ?? 0)
            .FirstOrDefault()
            ?? UI.GetSelectedEpisode();

        if (last != null)
        {
            UI.SelectFeed(last.FeedId);
            UI.SetEpisodesForFeed(last.FeedId, Data.Episodes);

            var list = Data.Episodes
                .Where(e => e.FeedId == last.FeedId)
                .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                .ToList();

            var idx = Math.Max(0, list.FindIndex(e => e.Id == last.Id));
            UI.SelectEpisodeIndex(idx);

            UI.ShowStartupEpisode(last, Data.Volume0_100, Data.Speed);
        }

        // === Snapshot → Player-UI ===
        Playback.SnapshotAvailable += snap => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (UI != null && Player != null)
                {
                    UI.UpdatePlayerSnapshot(snap, Player.State.Volume0_100);

                    var nowId = UI.GetNowPlayingId();
                    if (nowId is Guid nid && snap.EpisodeId == nid)
                    {
                        var ep = Data.Episodes.FirstOrDefault(x => x.Id == nid);
                        if (ep != null)
                            UI.SetWindowTitle((!Data.NetworkOnline ? "[OFFLINE] " : "") + (ep.Title ?? "—"));
                    }
                }
            }
            catch { }
        });

        Playback.StatusChanged += st => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (UI == null) return;
                switch (st)
                {
                    case PlaybackStatus.Loading:
                        UI.SetPlayerLoading(true, "Loading…", null);
                        break;
                    case PlaybackStatus.SlowNetwork:
                        UI.SetPlayerLoading(true, "Connecting… (slow)", null);
                        break;
                    case PlaybackStatus.Playing:
                    case PlaybackStatus.Ended:
                    default:
                        UI.SetPlayerLoading(false);
                        break;
                }
            }
            catch { }
        });

        Player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (UI != null && Playback != null)
                {
                    Playback.PersistProgressTick(
                        s,
                        eps => {
                            var fid = UI.GetSelectedFeedId();
                            if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
                        },
                        Data.Episodes);
                }
            }
            catch { }
        });

        _uiTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            try
            {
                if (UI != null && Player != null && Playback != null)
                {
                    Playback.PersistProgressTick(
                        Player.State,
                        eps => {
                            var fid = UI.GetSelectedFeedId();
                            if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
                        },
                        Data.Episodes);
                }
            }
            catch { }

            return true;
        });

        // ---------- APPLY CLI FLAGS (post-UI) ----------
        Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (UI == null || Player == null || Playback == null || Downloader == null) return;

                // --offline
                if (cli.Offline)
                {
                    CommandRouter.Handle(":net offline", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }

                // --engine (falls zur Laufzeit erneut gesetzt werden soll)
                if (!string.IsNullOrWhiteSpace(cli.Engine))
                {
                    CommandRouter.Handle($":engine {cli.Engine}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }

                // --opml-export
                if (!string.IsNullOrWhiteSpace(cli.OpmlExport))
                {
                    var path = cli.OpmlExport!;
                    CommandRouter.Handle($":opml export {path}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }

                // --opml-import (+ --import-mode)
                if (!string.IsNullOrWhiteSpace(cli.OpmlImport))
                {
                    var mode = (cli.OpmlImportMode ?? "merge").Trim().ToLowerInvariant();

                    if (mode == "dry-run")
                    {
                        try
                        {
                            var xml = OpmlIo.ReadFile(cli.OpmlImport!);
                            var doc = OpmlParser.Parse(xml);
                            var plan = OpmlImportPlanner.Plan(doc, Data.Feeds, updateTitles: false);
                            UI.ShowOsd($"OPML dry-run → new {plan.NewCount}, dup {plan.DuplicateCount}, invalid {plan.InvalidCount}", 2400);
                        }
                        catch (Exception ex)
                        {
                            UI.ShowOsd($"OPML dry-run failed: {ex.Message}", 2000);
                        }
                    }
                    else
                    {
                        if (mode == "replace")
                        {
                            // Bestehende Feeds + Episoden leeren
                            Data.Feeds.Clear();
                            Data.Episodes.Clear();
                            Data.LastSelectedFeedId = UI.AllFeedId;
                            _ = SaveAsync();

                            // UI entsprechend leeren
                            UI.SetFeeds(Data.Feeds, Data.LastSelectedFeedId);
                            CommandRouter.ApplyList(UI, Data);
                        }

                        // Merge/Replace arbeiten über vorhandenen :opml import Mechanismus
                        var path = cli.OpmlImport!.Contains(' ') ? $"\"{cli.OpmlImport}\"" : cli.OpmlImport!;
                        CommandRouter.Handle($":opml import {path}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                    }
                }

                // --feed
                if (!string.IsNullOrWhiteSpace(cli.Feed))
                {
                    var f = cli.Feed!.Trim();
                    // akzeptiere Schlüsselwörter und GUIDs 1:1
                    CommandRouter.Handle($":feed {f}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }

                // --search
                if (!string.IsNullOrWhiteSpace(cli.Search))
                {
                    CommandRouter.Handle($":search {cli.Search}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }
            }
            catch { /* robust */ }
        });

        try { Application.Run(); }
        finally
        {
            try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }
            try { if (_netTimerToken is not null) Application.MainLoop.RemoveTimeout(_netTimerToken); } catch {}

            try { Player?.Stop(); } catch { }
            (Player as IDisposable)?.Dispose();

            try { Downloader?.Dispose(); } catch { }

            if (!SkipSaveOnExit)
            {
                await SaveAsync();
            }
            try { Application.Shutdown(); } catch { }
            TerminalUtil.ResetHard();
            try { Log.CloseAndFlush(); } catch { }
            try { _probeHttp.Dispose(); } catch { }
        }
    }

    static IEnumerable<Episode> ApplySort(IEnumerable<Episode> eps, AppData data)
    {
        if (eps == null) return Enumerable.Empty<Episode>();
        var by  = (data.SortBy  ?? "pubdate").Trim().ToLowerInvariant();
        var dir = (data.SortDir ?? "desc").Trim().ToLowerInvariant();
        bool desc = dir == "desc";

        static double Progress(Episode e)
        {
            var pos = (double)(e.LastPosMs ?? 0);
            var len = (double)(e.LengthMs  ?? 0);
            if (len <= 0) return 0.0;
            var r = pos / Math.Max(len, pos);
            return Math.Clamp(r, 0.0, 1.0);
        }

        string FeedTitle(Episode e)
        {
            var f = Data.Feeds.FirstOrDefault(x => x.Id == e.FeedId);
            return f?.Title ?? "";
        }

        IOrderedEnumerable<Episode> ordered;

        switch (by)
        {
            case "title":
                ordered = desc
                    ? eps.OrderByDescending(e => e.Title ?? "", StringComparer.OrdinalIgnoreCase)
                    : eps.OrderBy(e => e.Title ?? "", StringComparer.OrdinalIgnoreCase);
                break;

            case "played":
                ordered = desc ? eps.OrderByDescending(e => e.Played)
                               : eps.OrderBy(e => e.Played);
                ordered = ordered.ThenByDescending(e => e.PubDate ?? DateTimeOffset.MinValue);
                break;

            case "progress":
                ordered = desc ? eps.OrderByDescending(Progress)
                               : eps.OrderBy(Progress);
                ordered = ordered.ThenByDescending(e => e.PubDate ?? DateTimeOffset.MinValue);
                break;

            case "feed":
                ordered = desc ? eps.OrderByDescending(FeedTitle, StringComparer.OrdinalIgnoreCase)
                               : eps.OrderBy(FeedTitle, StringComparer.OrdinalIgnoreCase);
                ordered = ordered.ThenByDescending(e => e.PubDate ?? DateTimeOffset.MinValue);
                break;

            case "pubdate":
            default:
                ordered = desc
                    ? eps.OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                    : eps.OrderBy(e => e.PubDate ?? DateTimeOffset.MinValue);
                break;
        }

        return ordered;
    }

    // Engine-Wechsel (hot swap)
    static async Task SwitchEngineAsync(string pref)
    {
        try
        {
            Data.PreferredEngine = string.IsNullOrWhiteSpace(pref) ? "auto" : pref.Trim().ToLowerInvariant();
            _ = SaveAsync();

            var next = PlayerFactory.Create(Data, out var info);
            ApplyPrefsTo(next);

            if (Player != null)
            {
                await Player.SwapToAsync(next, old => { try { old.Stop(); } catch { } });
                UI?.ShowOsd($"engine switched → {Player.Name}", 1400);

                if (Playback != null)
                {
                    Playback.PersistProgressTick(
                        Player.State,
                        eps => {
                            var fid = UI?.GetSelectedFeedId();
                            if (fid != null && UI != null) UI.SetEpisodesForFeed(fid.Value, eps);
                        },
                        Data.Episodes);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "engine switch failed");
            UI?.ShowOsd("engine switch failed", 1500);
        }
    }

    static void ApplyPrefsTo(IPlayer p)
    {
        try
        {
            if ((p.Capabilities & PlayerCapabilities.Volume) != 0)
            {
                var v = Math.Clamp(Data.Volume0_100, 0, 100);
                p.SetVolume(v);
            }
        } catch { }

        try
        {
            if ((p.Capabilities & PlayerCapabilities.Speed) != 0)
            {
                var s = Data.Speed;
                if (s <= 0) s = 1.0;
                p.SetSpeed(Math.Clamp(s, 0.25, 3.0));
            }
        } catch { }
    }

    static async Task SaveAsync()
    {
        const int MIN_INTERVAL_MS = 1000;

        void ScheduleDelayed()
        {
            if (_savePending) return;
            _savePending = true;

            _ = Task.Run(async () =>
            {
                await Task.Delay(MIN_INTERVAL_MS);
                lock (_saveGate)
                {
                    _savePending = false;
                    if (_saveRunning) return;
                    _saveRunning = true;
                }

                try { AppStorage.SaveAsync(Data).GetAwaiter().GetResult(); }
                catch (Exception ex) { Log.Debug(ex, "save (delayed) failed"); }
                finally
                {
                    lock (_saveGate)
                    {
                        _lastSave = DateTimeOffset.Now;
                        _saveRunning = false;
                    }
                }
            });
        }

        lock (_saveGate)
        {
            var now = DateTimeOffset.Now;
            var since = now - _lastSave;

            if (!_saveRunning && since.TotalMilliseconds >= MIN_INTERVAL_MS)
            {
                _saveRunning = true;
            }
            else
            {
                ScheduleDelayed();
                return;
            }
        }

        try { await AppStorage.SaveAsync(Data); }
        catch (Exception ex) { Log.Debug(ex, "save failed"); }
        finally
        {
            lock (_saveGate)
            {
                _lastSave = DateTimeOffset.Now;
                _saveRunning = false;
            }
        }
    }

    static void TriggerNetProbe()
    {
        if (_netProbeRunning) return;
        _netProbeRunning = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var probeOnline = await QuickNetCheckAsync().ConfigureAwait(false);

                if (probeOnline) { _netConsecOk++;  _netConsecFail = 0; }
                else             { _netConsecFail++; _netConsecOk   = 0; }

                bool state   = Data.NetworkOnline;
                bool dwellOk = (DateTimeOffset.UtcNow - _netLastFlip) >= _netMinDwell;

                bool flipToOn  = !state && probeOnline && _netConsecOk   >= SUCC_FOR_ONLINE && dwellOk;
                bool flipToOff =  state && !probeOnline && _netConsecFail >= FAILS_FOR_OFFLINE && dwellOk;

                Log.Information("net/decision prev={Prev} probe={Probe} ok={Ok} fail={Fail} dwellOk={DwellOk} age={Age}s flipOn={FlipOn} flipOff={FlipOff}",
                    state ? "online" : "offline",
                    probeOnline ? "online" : "offline",
                    _netConsecOk, _netConsecFail, dwellOk,
                    (DateTimeOffset.UtcNow - _netLastFlip).TotalSeconds.ToString("0"),
                    flipToOn, flipToOff);

                if (flipToOn || flipToOff)
                {
                    _netLastFlip = DateTimeOffset.UtcNow;
                    LogNicsSnapshot();
                    OnNetworkChanged(flipToOn);
                }
            }
            finally { _netProbeRunning = false; }
        });
    }

    static void QuitApp()
    {
        if (System.Threading.Interlocked.Exchange(ref _exitOnce, 1) == 1) return;

        try { if (_netTimerToken is not null) Application.MainLoop?.RemoveTimeout(_netTimerToken); } catch { }
        try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }

        try { Downloader?.Stop(); } catch { }
        try { Player?.Stop(); } catch { }

        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                try { Application.RequestStop(); } catch { }
            });
        }
        catch { }

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500).ConfigureAwait(false);
            try { Environment.Exit(0); } catch { }
        });
    }

    static void ConfigureLogging(string? level)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        // Map CLI level
        var min = Serilog.Events.LogEventLevel.Debug;
        switch ((level ?? "").Trim().ToLowerInvariant())
        {
            case "info":  min = Serilog.Events.LogEventLevel.Information; break;
            case "warn":
            case "warning": min = Serilog.Events.LogEventLevel.Warning; break;
            case "error": min = Serilog.Events.LogEventLevel.Error; break;
            case "debug":
            default:      min = Serilog.Events.LogEventLevel.Debug; break;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(min)
            .Enrich.WithProperty("pid", Environment.ProcessId)
            .WriteTo.Sink(MemLog)
            .WriteTo.File(
                Path.Combine(logDir, "podliner-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Exception}{NewLine}"
            )
            .CreateLogger();
    }

    static void InstallGlobalErrorHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "UnhandledException (IsTerminating={Terminating})", e.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Fatal(e.Exception, "UnobservedTaskException");
            e.SetObserved();
        };
    }

    static bool HasFeedWithUrl(string url)
    {
        return Data.Feeds.Any(f =>
        {
            var t = f.GetType();
            var prop = t.GetProperty("Url")
                       ?? t.GetProperty("FeedUrl")
                       ?? t.GetProperty("XmlUrl")
                       ?? t.GetProperty("SourceUrl")
                       ?? t.GetProperty("RssUrl");
            var val = prop?.GetValue(f) as string;
            return val != null && string.Equals(val, url, StringComparison.OrdinalIgnoreCase);
        });
    }

    static void ResetAutoAdvance() { }

    // ---------- Windows VT/UTF-8 Enable ----------
    static void EnableWindowsConsoleAnsi()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            const int STD_OUTPUT_HANDLE = -11;
            const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
            const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

            if (GetConsoleMode(handle, out uint mode))
            {
                uint newMode = mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;
                SetConsoleMode(handle, newMode);
            }
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    
    static void PrintVersion()
    {
        var asm = typeof(Program).Assembly;
        var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver  = string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() ?? "0.0.0" : info;

        string rid;
        try { rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier; }
        catch { rid = $"{Environment.OSVersion.Platform}-{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}".ToLowerInvariant(); }

        Console.WriteLine($"podliner {ver} ({rid})");
    }
    
    static void PrintHelp()
    {
        PrintVersion();
        Console.WriteLine();
        Console.WriteLine("Usage: podliner [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --version, -v            Show version and exit");
        Console.WriteLine("  --help, -h               Show this help and exit");
        Console.WriteLine("  --engine <auto|vlc|mpv|ffplay>");
        Console.WriteLine("  --theme <base|accent|native|auto>");
        Console.WriteLine("  --feed <all|saved|downloaded|history|queue|GUID>");
        Console.WriteLine("  --search \"<term>\"");
        Console.WriteLine("  --opml-import <FILE> [--import-mode merge|replace|dry-run]");
        Console.WriteLine("  --opml-export <FILE>");
        Console.WriteLine("  --offline");
        Console.WriteLine("  --ascii");
        Console.WriteLine("  --log-level <debug|info|warn|error>");
    }
}
