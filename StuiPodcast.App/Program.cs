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
using StuiPodcast.App;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using System.Text;
using System.Runtime.InteropServices;

class Program
{
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

        // Low-level TCP first (DNS-frei)
        var tcpOk =
            await TcpCheckAsync("1.1.1.1", 443, 900).ConfigureAwait(false) ||
            await TcpCheckAsync("8.8.8.8", 53, 900).ConfigureAwait(false);

        // HTTP (Captive-Portal-tauglich)
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
            // UI könnte beim ersten Probe-Result noch null sein
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

            if (!online) UI.ShowOsd("net: offline", 800); // online → kein OSD
        });

        _ = SaveAsync();
    }

    static AppData Data = new();
    static FeedService? Feeds;

    static SwappablePlayer? Player;
    static string? _initialEngineInfo; // für OSD nach UI-Build

    static Shell? UI;
    static PlaybackCoordinator? Playback;
    static MemoryLogSink MemLog = new(2000);

    static object? _uiTimer;
    static int _exitOnce = 0;

    static DownloadManager? Downloader;

    static async Task Main()
    {
        // --- Windows-Konsole für Unicode/ANSI fit machen ---
        EnableWindowsConsoleAnsi();

        ConfigureLogging();
        InstallGlobalErrorHandlers();

        Data  = await AppStorage.LoadAsync();
        Feeds = new FeedService(Data);

        _ = Task.Run(async () =>
        {
            bool online = await QuickNetCheckAsync();
            _netConsecOk   = online ? 1 : 0;
            _netConsecFail = online ? 0 : 1;
            _netLastFlip   = DateTimeOffset.UtcNow; // Dwell startet jetzt
            OnNetworkChanged(online);
        });

        /*
        // Default-Feed beim allerersten Start
        if (Data.Feeds.Count == 0)
        {
            try { await Feeds.AddFeedAsync("https://themadestages.podigee.io/feed/mp3"); }
            catch (Exception ex) { Log.Warning(ex, "Could not add default feed"); }
        }

        // Anchor-Feed nur hinzufügen, wenn noch nicht da
        var anchorUrl = "https://anchor.fm/s/fc0e8c18/podcast/rss";
        try
        {
            if (!HasFeedWithUrl(anchorUrl))
                await Feeds!.AddFeedAsync(anchorUrl);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not add anchor feed"); }

        */

        // 1) Core-Engine erzeugen …
        try
        {
            var core = PlayerFactory.Create(Data, out var engineInfo);

            // 2) … und als SwappablePlayer wrappen
            Player = new SwappablePlayer(core);

            // UI existiert hier noch nicht -> Text merken, OSD erst nach UI.Build()
            _initialEngineInfo = engineInfo;
        }
        catch (Exception)
        {
            UI?.ShowOsd("No audio engine found", 2000);
            throw;
        }

        // 3) Coordinator bekommt den SwappablePlayer (implementiert IPlayer)
        Playback = new PlaybackCoordinator(Data, Player, SaveAsync, MemLog);

        Downloader = new DownloadManager(Data);

        // Auto-Advance: EINZIGE Quelle ist der Coordinator
        Playback.AutoAdvanceSuggested += next =>
        {
            if (UI == null || Playback == null) return;

            var list = Data.Episodes
                .Where(e => e.FeedId == next.FeedId)
                .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                .ToList();

            var i = list.FindIndex(e => e.Id == next.Id);
            if (i >= 0) UI.SelectEpisodeIndex(i);

            Playback.Play(next);
            UI.SetWindowTitle((!Data.NetworkOnline ? "[OFFLINE] " : "") + next.Title);
            UI.ShowDetails(next);
            UI.SetNowPlaying(next.Id);
        };

        // >> Restore Player prefs (Truth = Player; Data = Snapshot)
        try
        {
            var v = Math.Clamp(Data.Volume0_100, 0, 100);
            if (v != 0 || Data.Volume0_100 == 0) Player.SetVolume(v);
            var s = Data.Speed;
            if (s <= 0) s = 1.0;
            Player.SetSpeed(Math.Clamp(s, 0.25, 3.0));
        } catch { /* falls ältere Player impls */ }

        Console.TreatControlCAsInput = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; QuitApp(); };
        AppDomain.CurrentDomain.ProcessExit += (_, __) => TerminalUtil.ResetHard();

        Application.Init();

        UI = new Shell(MemLog);
        UI.Build();

        if (!string.IsNullOrEmpty(_initialEngineInfo))
            UI.ShowOsd(_initialEngineInfo, 1200);

        // sofort einmal prüfen – nichts zuweisen, einfach ausführen
        _ = Task.Run(async () =>
        {
            var online = await QuickNetCheckAsync();
            OnNetworkChanged(online);
        });

        try
        {
            NetworkChange.NetworkAvailabilityChanged += (s, e) =>
            {
                // OS-Event ist nur ein Hint → echte Entscheidung trifft die Probe + Hysterese
                TriggerNetProbe();
            };
        }
        catch { /* kann auf manchen Plattformen fehlen → egal */ }

        _netTimerToken = Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(3), _ =>
        {
            TriggerNetProbe();
            return true; // weiterlaufen
        });

        // Lookups
        UI.SetQueueLookup(id => Data.Queue.Contains(id));
        UI.SetDownloadStateLookup(id => Data.DownloadMap.TryGetValue(id, out var s) ? s.State : DownloadState.None);
        UI.SetOfflineLookup(() => !Data.NetworkOnline); // true = offline

        // Download-Status → UI
        Downloader.StatusChanged += (id, st) =>
        {
            var prev = _dlLast.TryGetValue(id, out var p) ? p : DownloadState.None;
            _dlLast[id] = st.State;

            // Nur wenn sich der STATE ändert, die UI refreshen (Badge wechselt)
            if (prev != st.State)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    UI?.RefreshEpisodesForSelectedFeed(Data.Episodes);

                    // Ultra-kurze OSDs, nur bei Übergängen – nie bei jedem Chunk
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

            // Gleichbleibender State (Running): Liste sanft pulsen
            if (st.State == DownloadState.Running && (DateTime.UtcNow - _dlLastUiPulse) > TimeSpan.FromMilliseconds(500))
            {
                _dlLastUiPulse = DateTime.UtcNow;
                Application.MainLoop?.Invoke(() => UI?.RefreshEpisodesForSelectedFeed(Data.Episodes));
            }
        };

        // Worker starten
        Downloader.EnsureRunning();

        UI.EpisodeSorter = eps => ApplySort(eps, Data);
        UI.SetHistoryLimit(Data.HistorySize);

        // >> Restore Player-Bar position & Filter
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
            CommandRouter.ApplyList(UI, Data); // respektiert UnplayedOnly
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
                    if (Data.LastSelectedEpisodeIndex is int legacy) idx = legacy; // alt
                }

                UI.SetEpisodesForFeed(fid.Value, Data.Episodes);
                UI.SelectEpisodeIndex(idx);
            }

            _ = SaveAsync();
        };

        // Auswahlwechsel → pro-Feed Index speichern
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

            // Verlauf stempeln
            ep.LastPlayedAt = DateTimeOffset.Now;
            _ = SaveAsync();

            // Queue-Feed: alle davor + diese entfernen
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

            // === Quelle bestimmen (ohne DownloadManager-API) ===
            string? localPath = null;
            if (Data.DownloadMap.TryGetValue(ep.Id, out var st)
                && st.State == DownloadState.Done
                && !string.IsNullOrWhiteSpace(st.LocalPath)
                && File.Exists(st.LocalPath))
            {
                localPath = st.LocalPath;
            }

            bool isRemote =
                string.IsNullOrWhiteSpace(localPath) &&           // kein lokaler Treffer
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
                _        => localPath ?? (online ? ep.AudioUrl : null) // auto: lokal bevorzugt, sonst remote wenn online
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

            // Minimale Injektion: ep.AudioUrl kurz auf gewählte Quelle setzen
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

            // Einmaliger Retry für lokale Dateien, falls der Start „hängt“.
            if (localPath != null)
            {
                Application.MainLoop?.AddTimeout(TimeSpan.FromMilliseconds(600), _ =>
                {
                    try
                    {
                        var s = Player.State;
                        if (!s.IsPlaying && s.Position == TimeSpan.Zero)
                        {
                            try { Player.Stop(); } catch { /* best effort */ }

                            var fileUri = new Uri(localPath).AbsoluteUri;

                            var old = ep.AudioUrl;
                            try
                            {
                                ep.AudioUrl = fileUri; // nur für den Start
                                Playback.Play(ep);
                                UI.ShowOsd("Retry (file://)");
                            }
                            finally
                            {
                                ep.AudioUrl = old; // wieder zurück
                            }
                        }
                    }
                    catch
                    {
                        /* robust bleiben */
                    }

                    return false; // one-shot
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
                if (ep.LengthMs is long len) ep.LastPosMs = len; // ✔ → ans Ende
                ep.LastPlayedAt = DateTimeOffset.Now;
            }
            else
            {
                ep.LastPosMs = 0; // ◯ → leerer Kreis
            }

            _ = SaveAsync();

            var fid = UI.GetSelectedFeedId();
            if (fid != null) UI.SetEpisodesForFeed(fid.Value, Data.Episodes);
            UI.ShowDetails(ep);
        };

        // Program.cs – Command-Handler
        UI.Command += cmd =>
        {
            if (UI == null || Player == null || Playback == null || Downloader == null) return;

            // 1) Queue-Commands zuerst
            if (CommandRouter.HandleQueue(cmd, UI, Data, SaveAsync))
            {
                UI.SetQueueOrder(Data.Queue);
                UI.RefreshEpisodesForSelectedFeed(Data.Episodes);
                return;
            }

            // 2) Download-Commands
            if (CommandRouter.HandleDownloads(cmd, UI, Data, Downloader, SaveAsync))
                return;

            // 3) Rest
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
        CommandRouter.ApplyList(UI, Data); // respektiert UnplayedOnly, behält Auswahl

        var initialFeed = UI.GetSelectedFeedId();
        if (initialFeed != null)
        {
            int idx = 0;
            if (!Data.LastSelectedEpisodeIndexByFeed.TryGetValue(initialFeed.Value, out idx))
            {
                if (Data.LastSelectedEpisodeIndex is int legacy) idx = legacy; // alt
            }

            UI.SetEpisodesForFeed(initialFeed.Value, Data.Episodes);
            UI.SelectEpisodeIndex(idx);
        }

        // zuletzt gespielte Episode (History-Priorität)
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

        // === Snapshot → Player-UI (einzige Quelle für Anzeige) ===
        Playback.SnapshotAvailable += snap => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (UI != null && Player != null)
                {
                    UI.UpdatePlayerSnapshot(snap, Player.State.Volume0_100);

                    // Optional: Fenstertitel für aktuelle Episode aktualisieren
                    var nowId = UI.GetNowPlayingId();
                    if (nowId is Guid nid && snap.EpisodeId == nid)
                    {
                        var ep = Data.Episodes.FirstOrDefault(x => x.Id == nid);
                        if (ep != null)
                            UI.SetWindowTitle((!Data.NetworkOnline ? "[OFFLINE] " : "") + (ep.Title ?? "—"));
                    }
                }
            }
            catch { /* UI robust halten */ }
        });

        // **NEU**: Playback-Status steuert das Loading-Banner sichtbar & korrekt
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
            catch { /* robust */ }
        });

        // Player-State treibt Persist/Auto-Advance (UI-Update kommt aus Snapshot)
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
            catch
            {
                // UI robust halten
            }
        });

        // UI-Refresh Watchdog (redundant, aber pragmatisch): nur Persist-Tick
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
            catch { /* robust bleiben */ }

            return true;
        });

        try { Application.Run(); }
        finally
        {
            try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }
            try { if (_netTimerToken is not null) Application.MainLoop.RemoveTimeout(_netTimerToken); } catch {}

            try { Player?.Stop(); } catch { }
            (Player as IDisposable)?.Dispose();

            try { Downloader?.Dispose(); } catch { }

            await SaveAsync();
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
            var r = pos / Math.Max(len, pos); // nie >1, nie NaN
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
            // Wunsch merken (falls PlayerFactory auf Data.PreferredEngine schaut)
            Data.PreferredEngine = string.IsNullOrWhiteSpace(pref) ? "auto" : pref.Trim().ToLowerInvariant();
            _ = SaveAsync();

            var next = PlayerFactory.Create(Data, out var info);
            ApplyPrefsTo(next);

            if (Player != null)
            {
                await Player.SwapToAsync(next, old => { try { old.Stop(); } catch { } });
                UI?.ShowOsd($"engine switched → {Player.Name}", 1400);

                // Nach Engine-Switch: Persist-Tick einmal triggern (UI snappt über SnapshotAvailable)
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
        if (_netProbeRunning) return;   // schon unterwegs → nix tun
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
                    LogNicsSnapshot();   // optional
                    OnNetworkChanged(flipToOn);
                }
            }
            finally { _netProbeRunning = false; }
        });
    }

    static void QuitApp()
    {
        if (System.Threading.Interlocked.Exchange(ref _exitOnce, 1) == 1) return;

        // 1) Hintergrundaktivitäten stoppen – NICHT warten
        try { if (_netTimerToken is not null) Application.MainLoop?.RemoveTimeout(_netTimerToken); } catch { }
        try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }

        try { Downloader?.Stop(); } catch { }     // lädt sofort keinen neuen Job mehr
        try { Player?.Stop(); } catch { }         // Player stoppen, aber nicht dispose’n

        // 2) UI-Loop beenden (vom UI-Thread)
        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                try { Application.RequestStop(); } catch { }
            });
        }
        catch { }

        // 3) Watchdog: falls Run() nicht sauber zurückkehrt -> hart beenden
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500).ConfigureAwait(false);
            try { Environment.Exit(0); } catch { }
        });
    }

    static void ConfigureLogging()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("pid", Environment.ProcessId)
            .WriteTo.Sink(MemLog)
            .WriteTo.File(
                Path.Combine(logDir, "stui-podcast-.log"),
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

    // Legacy-Compat: aktuell No-Op (wurde früher für End-Transition genutzt)
    static void ResetAutoAdvance() { }

    // ---------- Windows VT/UTF-8 Enable ----------
    static void EnableWindowsConsoleAnsi()
    {
        try
        {
            // UTF-8 ohne BOM für saubere Glyphen
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
        catch { /* best effort */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
