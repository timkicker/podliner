using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using Terminal.Gui;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;

using StuiPodcast.App;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Player;
using StuiPodcast.Infra.Opml;
using ThemeMode = StuiPodcast.App.UI.Shell.ThemeMode;

class Program
{
    // ===== Runtime Flags =====
    internal static bool SkipSaveOnExit = false;
    static DateTimeOffset _netLastHeartbeat = DateTimeOffset.MinValue;
    static readonly TimeSpan _netHeartbeatEvery = TimeSpan.FromMinutes(2);


    // ===== CLI parsed options =====
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
                case "-V": o.ShowVersion = true; break;

                case "--help":
                case "-h":
                case "-?": o.ShowHelp = true; break;

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

                case "--offline": o.Offline = true; break;
                case "--ascii":   o.Ascii   = true; break;

                case "--log-level":
                    if (i + 1 < args.Length) o.LogLevel = args[++i].Trim().ToLowerInvariant();
                    break;
            }
        }
        return o;
    }

    // ===== Global singletons for this program =====
    static AppFacade?      App;          // neue Fassade (Persistenz + Download-ReadModel)
    static ConfigStore?    ConfigStore;  // appsettings.json
    static LibraryStore?   LibraryStore; // library/library.json

    static AppData         Data = new(); // UI-kompatibler Laufzeit-State (Bridge)
    static FeedService?    Feeds;

    static SwappablePlayer?   Player;
    static string?            _initialEngineInfo;
    static Shell?             UI;
    static PlaybackCoordinator? Playback;
    static MemoryLogSink      MemLog = new(2000);
    static DownloadManager?   Downloader;

    // ===== Timers / State =====
    static object? _uiTimer;
    static object? _netTimerToken;
    static int     _exitOnce = 0;

    // Save throttle (AppFacade)
    static readonly object _saveGate = new();
    static DateTimeOffset  _lastSave = DateTimeOffset.MinValue;
    static bool            _savePending = false;
    static bool            _saveRunning = false;

    // Download-UI pulse (nur für UI refresh)
    static DateTime _dlLastUiPulse = DateTime.MinValue;

    // Network probing
    static volatile bool _netProbeRunning = false;
    static int _netConsecOk = 0, _netConsecFail = 0;
    const int FAILS_FOR_OFFLINE = 4;
    const int SUCC_FOR_ONLINE   = 3;
    static DateTimeOffset _netLastFlip = DateTimeOffset.MinValue;
    static readonly TimeSpan _netMinDwell = TimeSpan.FromSeconds(15);
    static readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

    
    public static bool IsDownloaded(Guid episodeId)
        => App?.IsDownloaded(episodeId) ?? false;

    public static bool TryGetLocalPath(Guid episodeId, out string? path)
    {
        if (App != null && App.TryGetLocalPath(episodeId, out path)) return true;
        path = null; return false;
    }

    
    // ===== Entry =====
    static async Task Main(string[]? args)
    {
        var cli = ParseArgs(args);

        if (cli.ShowVersion) { PrintVersion(); return; }
        if (cli.ShowHelp)    { PrintHelp();    return; }

        EnableWindowsConsoleAnsi();
        if (cli.Ascii) { try { GlyphSet.Use(GlyphSet.Profile.Ascii); } catch { } }

        ConfigureLogging(cli.LogLevel);
        InstallGlobalErrorHandlers();

        // ---- Composition Root: Stores + Facade ----
        var baseConfigDir =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)                 // %APPDATA%
                : (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       ".config"));                                                      // ~/.config
        var appConfigDir = Path.Combine(baseConfigDir, "podliner");

        ConfigStore  = new ConfigStore(appConfigDir);
        LibraryStore = new LibraryStore(appConfigDir, subFolder: "", fileName: "library.json");

        
        Console.WriteLine($"Config:  {ConfigStore.FilePath}");
        Console.WriteLine($"Library: {LibraryStore.FilePath}");


        // Download-Adapter (nur read) auf DownloadManager (write) – Manager bekommt später AppData
        Downloader   = new DownloadManager(Data, appConfigDir); // appConfigDir hast du oben bereits berechnet
        var downloadLookup = new DownloadLookupAdapter(Downloader!, Data);

        App = new AppFacade(ConfigStore, LibraryStore, downloadLookup);

        // ---- Laden & Bridge → AppData (UI bleibt unverändert) ----
        var cfg = App.LoadConfig();
        var lib = App.LoadLibrary();
        Log.Information("loaded config theme={Theme} playerAtTop={PlayerTop} sort={SortBy}/{SortDir}",
            cfg.Theme, cfg.Ui.PlayerAtTop, cfg.ViewDefaults.SortBy, cfg.ViewDefaults.SortDir);

        Log.Information("loaded library feeds={FeedCount} episodes={EpCount} queue={QCount} history={HCount}",
            lib.Feeds.Count, lib.Episodes.Count, lib.Queue?.Count ?? 0, lib.History?.Count ?? 0);

        Bridge.SyncFromFacadeToAppData(App, Data);

        // CLI: Engine-Präferenz vor Player-Erzeugung übernehmen
        if (!string.IsNullOrWhiteSpace(cli.Engine))
            Data.PreferredEngine = cli.Engine!.Trim().ToLowerInvariant();

        // Erste Netzwerkprobe (async)
        _ = Task.Run(async () =>
        {
            bool online = await QuickNetCheckAsync();
            _netConsecOk   = online ? 1 : 0;
            _netConsecFail = online ? 0 : 1;
            _netLastFlip   = DateTimeOffset.UtcNow;
            OnNetworkChanged(online);
        });

        // ---- Player-Engine erzeugen ----
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

        // ---- Coordinator ----
        Playback  = new PlaybackCoordinator(Data, Player, SaveAsync, MemLog);
        Feeds     = new FeedService(Data, App);

        Log.Information("cfg at {Cfg}", ConfigStore.FilePath);
        Log.Information("lib at {Lib}", LibraryStore.FilePath);

        
        // ---- Player Prefs anwenden (aus Config via Bridge) ----
        try
        {
            var v = Math.Clamp(Data.Volume0_100, 0, 100);
            if (v != 0 || Data.Volume0_100 == 0) Player.SetVolume(v);
            var s = Data.Speed; if (s <= 0) s = 1.0;
            Player.SetSpeed(Math.Clamp(s, 0.25, 3.0));
        } catch { }

        Console.TreatControlCAsInput = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; QuitApp(); };
        AppDomain.CurrentDomain.ProcessExit += (_, __) => TerminalUtil.ResetHard();

        // ---- UI ----
        Application.Init();
        UI = new Shell(MemLog);

        // Startfeed („All“) als initiale Selektion
        try { Data.LastSelectedFeedId = UI.AllFeedId; } catch { }

        UI.Build();
        UpdateWindowTitleWithDownloads();

        // Theme via CLI?
        if (!string.IsNullOrWhiteSpace(cli.Theme))
        {
            var t = cli.Theme!.Trim().ToLowerInvariant();
            var cliAskedAuto = t == "auto";

            ThemeMode tm = t switch
            {
                "base"   => ThemeMode.Base,
                "accent" => ThemeMode.MenuAccent,
                "native" => ThemeMode.Native,
                "user"   => ThemeMode.User,
                "auto"   => ThemeMode.User,              // << default now "user"
                _        => ThemeMode.User               // << fallback to "user"
            };

            try
            {
                UI.SetTheme(tm);
                // WICHTIG: Wenn der Nutzer "auto" gesetzt hat, auch "auto" PERSISTIEREN.
                Data.ThemePref = cliAskedAuto ? "auto" : tm.ToString();
                _ = SaveAsync();
            }
            catch { }
        }
        else
        {
            // Keine CLI-Vorgabe → gespeicherten Wert lesen.
            // Ist er "auto" oder leer, nimm das User-Theme als Default,
            // aber überschreibe ThemePref NICHT (bleibt "auto").
            var pref = (Data.ThemePref ?? "auto").Trim();

            ThemeMode desired =
                pref.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? ThemeMode.User
                    : (Enum.TryParse<ThemeMode>(pref, out var saved)
                        ? saved
                        : ThemeMode.User);

            UI.SetTheme(desired);
            Log.Information("theme resolved saved={Saved} default={Default} chosen={Chosen}",
                Data.ThemePref, "User", desired);

            // HIER NICHT Data.ThemePref ändern – "auto" bleibt "auto",
            // bis der User aktiv ein Theme setzt.
        }


        // Feeds/Episoden initial zeigen
        Application.MainLoop?.AddIdle(() =>
        {
            try { UI.EnsureSelectedFeedVisibleAndTop(); } catch { }
            return false;
        });
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
        _netTimerToken = Application.MainLoop.AddTimeout(NetProbeInterval(), _ =>
        {
            TriggerNetProbe();
            // nächstes Intervall dynamisch setzen
            Application.MainLoop.AddTimeout(NetProbeInterval(), __ =>
            {
                TriggerNetProbe();
                return true; // weiter wiederholen
            });
            return false; // diesen ersten Timeout nicht wiederholen
        });


        // Lookups für UI (Queue/Downloaded/Offline)
        UI.SetQueueLookup(id => Data.Queue.Contains(id));
        UI.SetDownloadStateLookup(id => App!.IsDownloaded(id) ? DownloadState.Done : DownloadState.None);
        UI.SetOfflineLookup(() => !Data.NetworkOnline);

        // DownloadManager → UI
        Downloader!.StatusChanged += (id, st) =>
        {
            // UI-Badge updates
            Application.MainLoop?.Invoke(() =>
            {
                UI?.RefreshEpisodesForSelectedFeed(Data.Episodes);
                UpdateWindowTitleWithDownloads();
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

            if (st.State == DownloadState.Running && (DateTime.UtcNow - _dlLastUiPulse) > TimeSpan.FromMilliseconds(500))
            {
                _dlLastUiPulse = DateTime.UtcNow;
                Application.MainLoop?.Invoke(() => UI?.RefreshEpisodesForSelectedFeed(Data.Episodes));
            }
        };
        Downloader.EnsureRunning();

        // Sortierung/Filter/Placement nach Config
        UI.EpisodeSorter = eps => ApplySort(eps, Data);
        UI.SetUnplayedHint(Data.UnplayedOnly);
        UI.SetPlayerPlacement(Data.PlayerAtTop);

        // Quit
        UI.QuitRequested += () => QuitApp();

        // Add Feed
        UI.AddFeedRequested += async url =>
        {
            if (UI == null || Feeds == null) return;
            if (string.IsNullOrWhiteSpace(url)) { UI.ShowOsd("Add feed: URL fehlt", 1500); return; }

            Log.Information("ui/addfeed url={Url}", url); // <--- NEU
            UI.ShowOsd("Adding feed…", 800);

            try
            {
                if (HasFeedWithUrl(url)) { UI.ShowOsd("Already added", 1200); return; }
            }
            catch { }

            try
            {
                var f = await Feeds.AddFeedAsync(url);
                App?.SaveNow();  // sofort in library.json schreiben
                Log.Information("ui/addfeed ok id={Id} title={Title}", f.Id, f.Title); // <--- NEU

                Data.LastSelectedFeedId = f.Id;
                // Index-Map ggf. leer; UI zeigt Liste neu
                _ = SaveAsync();

                UI.SetFeeds(Data.Feeds, f.Id);
                UI.SetEpisodesForFeed(f.Id, Data.Episodes);
                UI.SelectEpisodeIndex(0);

                UI.ShowOsd("Feed added ✓", 1200);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ui/addfeed failed url={Url}", url); // <--- NEU
                UI.ShowOsd($"Add failed: {ex.Message}", 2200);
            }
        };

        // Refresh
        UI.RefreshRequested += async () =>
        {
            await Feeds!.RefreshAllAsync();

            var selected = UI.GetSelectedFeedId() ?? Data.LastSelectedFeedId;
            UI.SetFeeds(Data.Feeds, selected);

            if (selected != null)
            {
                UI.SetEpisodesForFeed(selected.Value, Data.Episodes);
                // Index-Remembering (optional)
            }
            CommandRouter.ApplyList(UI, Data);
        };

        // Selection changed
        UI.SelectedFeedChanged += () =>
        {
            var fid = UI.GetSelectedFeedId();
            Data.LastSelectedFeedId = fid;

            if (fid != null)
            {
                UI.SetEpisodesForFeed(fid.Value, Data.Episodes);
                UI.SelectEpisodeIndex(0);
            }

            _ = SaveAsync();
        };

        // EpisodeSelectionChanged (Index merken optional)
        UI.EpisodeSelectionChanged += () =>
        {
            _ = SaveAsync();
        };

        // Play selected
        UI.PlaySelected += () =>
        {
            var ep = UI?.GetSelectedEpisode();
            if (ep == null || Player == null || Playback == null || UI == null) return;

            var curFeed = UI.GetSelectedFeedId();

            ep.Progress.LastPlayedAt = DateTimeOffset.UtcNow;
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
            if (App!.TryGetLocalPath(ep.Id, out var lp)) localPath = lp;

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
                var msg = (localPath == null) ? "∅ Offline: not downloaded" : "No playable source";
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

        // Manuell „gespielt“ toggeln (neue Felder)
        UI.TogglePlayedRequested += () =>
        {
            var ep = UI?.GetSelectedEpisode();
            if (ep == null) return;

            ep.ManuallyMarkedPlayed = !ep.ManuallyMarkedPlayed;

            if (ep.ManuallyMarkedPlayed)
            {
                if (ep.DurationMs > 0) ep.Progress.LastPosMs = ep.DurationMs;
                ep.Progress.LastPlayedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                ep.Progress.LastPosMs = 0;
            }

            _ = SaveAsync();

            var fid = UI.GetSelectedFeedId();
            if (fid != null) UI.SetEpisodesForFeed(fid.Value, Data.Episodes);
            UI.ShowDetails(ep);
        };

        // Commands
        UI.Command += cmd =>
        {
            Log.Debug("cmd {Cmd}", cmd); // <--- NEU
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

        // Suche
        UI.SearchApplied += query =>
        {
            var fid = UI?.GetSelectedFeedId();
            IEnumerable<Episode> list = Data.Episodes;
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
            UI.SetEpisodesForFeed(initialFeed.Value, Data.Episodes);
            UI.SelectEpisodeIndex(0);
        }

        // „Zuletzt gehört“ heuristisch bestimmen (neue Felder)
        var last = Data.Episodes
            .OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Progress.LastPosMs)
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

        // Snapshot → Player-UI
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

                // --engine (zur Laufzeit erneut setzen)
                if (!string.IsNullOrWhiteSpace(cli.Engine))
                {
                    CommandRouter.Handle($":engine {cli.Engine}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }

                // --opml-export
                if (!string.IsNullOrWhiteSpace(cli.OpmlExport))
                {
                    var path = cli.OpmlExport!;
                    Log.Information("cli/opml export path={Path}", path); // <--- NEU
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
                            Log.Information("cli/opml dryrun path={Path} new={New} dup={Dup} invalid={Invalid}",
                                cli.OpmlImport, plan.NewCount, plan.DuplicateCount, plan.InvalidCount); // <--- NEU
                            UI.ShowOsd($"OPML dry-run → new {plan.NewCount}, dup {plan.DuplicateCount}, invalid {plan.InvalidCount}", 2400);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "cli/opml dryrun failed path={Path}", cli.OpmlImport); // <--- NEU
                            UI.ShowOsd($"OPML dry-run failed: {ex.Message}", 2000);
                        }
                    }
                    else
                    {
                        Log.Information("cli/opml import path={Path} mode={Mode}", cli.OpmlImport, mode); // <--- NEU
                        if (mode == "replace")
                        {
                            Log.Information("cli/opml replace clearing existing feeds/episodes"); // <--- NEU
                            // Bestehende Feeds + Episoden leeren (UI-Logik bleibt)
                            Data.Feeds.Clear();
                            Data.Episodes.Clear();
                            Data.LastSelectedFeedId = UI.AllFeedId;
                            _ = SaveAsync();

                            UI.SetFeeds(Data.Feeds, Data.LastSelectedFeedId);
                            CommandRouter.ApplyList(UI, Data);
                        }

                        var path = cli.OpmlImport!.Contains(' ') ? $"\"{cli.OpmlImport}\"" : cli.OpmlImport!;
                        CommandRouter.Handle($":opml import {path}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                    }
                }

                // --feed
                if (!string.IsNullOrWhiteSpace(cli.Feed))
                {
                    var f = cli.Feed!.Trim();
                    CommandRouter.Handle($":feed {f}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }

                // --search
                if (!string.IsNullOrWhiteSpace(cli.Search))
                {
                    CommandRouter.Handle($":search {cli.Search}", Player, Playback, UI, MemLog, Data, SaveAsync, Downloader, SwitchEngineAsync);
                }
            }
            catch { }
        });

        try { Application.Run(); }
        finally
        {
            Log.Information("shutdown begin"); // <--- NEU
            try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }
            try { if (_netTimerToken is not null) Application.MainLoop.RemoveTimeout(_netTimerToken); } catch {}

            try { Player?.Stop(); } catch { }
            (Player as IDisposable)?.Dispose();

            try { Downloader?.Dispose(); } catch { }

            if (!SkipSaveOnExit) { await SaveAsync(); }

            try { Application.Shutdown(); } catch { }
            TerminalUtil.ResetHard();
            try { Log.CloseAndFlush(); } catch { }
            try { _probeHttp.Dispose(); } catch { }
            Log.Information("shutdown end");   // <--- NEU
        }
    }

    // ===== Helpers =====
    static ThemeMode DefaultThemeForPlatform()
        => OperatingSystem.IsWindows() ? ThemeMode.Base
            : OperatingSystem.IsMacOS()   ? ThemeMode.Native
            : ThemeMode.MenuAccent;


    static void UpdateWindowTitleWithDownloads()
    {
        if (UI == null) return;

        var offlinePrefix = (!Data.NetworkOnline) ? "[OFFLINE] " : "";
        string baseTitle = "Podliner";
        try
        {
            var nowId = UI.GetNowPlayingId();
            if (nowId != null)
            {
                var ep = Data.Episodes.FirstOrDefault(x => x.Id == nowId);
                if (ep != null && !string.IsNullOrWhiteSpace(ep.Title))
                    baseTitle = ep.Title!;
            }
        }
        catch { }

        // HUD (Wir zeigen nur "⇣" wenn irgendwas läuft – Detailzähler sind Aufgabe des DownloadManagers)
        string hud = "";
        // Optional: wenn du Zählwerte brauchst, ergänze DownloadManager-API dafür.

        UI.SetWindowTitle($"{offlinePrefix}{baseTitle}{hud}");
    }

    static IEnumerable<Episode> ApplySort(IEnumerable<Episode> eps, AppData data)
    {
        if (eps == null) return Enumerable.Empty<Episode>();
        var by  = (data.SortBy  ?? "pubdate").Trim().ToLowerInvariant();
        var dir = (data.SortDir ?? "desc").Trim().ToLowerInvariant();
        bool desc = dir == "desc";

        static double Progress(Episode e)
        {
            var pos = (double)(e.Progress?.LastPosMs ?? 0);
            var len = (double)(e.DurationMs);
            if (len <= 0) return 0.0;
            var r = pos / Math.Max(len, 1);
            return Math.Clamp(r, 0.0, 1.0);
        }

        static bool IsPlayed(Episode e)
        {
            if (e.ManuallyMarkedPlayed) return true;
            var len = e.DurationMs;
            if (len <= 0) return false;
            var pos = e.Progress?.LastPosMs ?? 0;
            // Heuristik (fix): fertig bei >=99.5% oder Rest <= 2s
            if (len <= 60_000) return (pos >= (long)(len * 0.98)) || (len - pos <= 500);
            return (pos >= (long)(len * 0.995)) || (len - pos <= 2000);
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
                ordered = desc ? eps.OrderByDescending(IsPlayed)
                               : eps.OrderBy(IsPlayed);
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
            Log.Information("engine created name={Engine} caps={Caps} info={Info}",
                Player?.Name, (Player?.Capabilities).ToString(), _initialEngineInfo);
            ApplyPrefsTo(next);

            if (Player != null)
            {
                await Player.SwapToAsync(next, old => { try { old.Stop(); } catch { } });
                Log.Information("engine switched current={Name} caps={Caps}", Player.Name, Player.Capabilities); // <--- NEU
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

    // Persistenz – Bridge zu AppFacade (kein AppStorage mehr)
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

                try
                {
                    if (App != null)
                    {
                        Bridge.SyncFromAppDataToFacade(Data, App);
                        App.SaveNow(); // explizit; wir schreiben bewusst deterministisch
                    }
                }
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

        try
        {
            if (App != null)
            {
                Bridge.SyncFromAppDataToFacade(Data, App);
                App.SaveNow();
            }
        }
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
                    Log.Information("net/state change → {State}", flipToOn ? "online" : "offline");
                    OnNetworkChanged(flipToOn);
                }
                else
                {
                    // Seltenen Heartbeat loggen (Debug), damit man Aktivität sieht
                    var now = DateTimeOffset.UtcNow;
                    if (now - _netLastHeartbeat >= _netHeartbeatEvery)
                    {
                        _netLastHeartbeat = now;
                        Log.Debug("net/steady state={State} ok={Ok} fail={Fail}",
                            Data.NetworkOnline ? "online" : "offline", _netConsecOk, _netConsecFail);
                    }
                }

            }
            finally { _netProbeRunning = false; }
        });
    }
    
    static TimeSpan NetProbeInterval()
        => Data.NetworkOnline ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(5);


    static void OnNetworkChanged(bool online)
    {
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

    // ===== Net probes =====
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
            Log.Verbose("net/probe tcp ok {Host}:{Port} in {Ms}ms", hostOrIp, port, ms);
            return true;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "net/probe tcp fail {Host}:{Port}", hostOrIp, port);
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
            Log.Verbose("net/probe http {Url} {Code} in {Ms}ms", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);
            return ((int)resp.StatusCode) is >= 200 and < 400;
        }
        catch (Exception exHead)
        {
            try
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await _probeHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                sw2.Stop();
                Log.Verbose("net/probe http[GET] {Url} {Code} in {Ms}ms (HEAD failed: {Err})",                    url, (int)resp.StatusCode, sw2.ElapsedMilliseconds, exHead.GetType().Name);
                return ((int)resp.StatusCode) is >= 200 and < 400;
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "net/probe http fail {Url}", url);
                return false;
            }
        }
    }

    static async Task<bool> QuickNetCheckAsync()
    {
        var swAll = System.Diagnostics.Stopwatch.StartNew();

        bool anyUp = false;
        try { anyUp = NetworkInterface.GetIsNetworkAvailable(); Log.Verbose("net/probe nics-available={Avail}", anyUp);
            ; } catch { }

        var tcpOk =
            await TcpCheckAsync("1.1.1.1", 443, 900).ConfigureAwait(false) ||
            await TcpCheckAsync("8.8.8.8", 53, 900).ConfigureAwait(false);

        var httpOk =
            await HttpProbeAsync("http://connectivitycheck.gstatic.com/generate_204").ConfigureAwait(false) ||
            await HttpProbeAsync("http://www.msftconnecttest.com/connecttest.txt").ConfigureAwait(false);

        swAll.Stop();
        Log.Verbose("net/probe result tcp={TcpOk} http={HttpOk} anyNicUp={NicUp} total={Ms}ms",
            tcpOk, httpOk, anyUp, swAll.ElapsedMilliseconds);

        var online = (tcpOk || httpOk) && anyUp;
        return online;
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

    static void QuitApp()
    {
        if (Interlocked.Exchange(ref _exitOnce, 1) == 1) return;

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

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            try { Environment.Exit(0); } catch { }
        });
    }

    static void ConfigureLogging(string? level)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        var min = Serilog.Events.LogEventLevel.Debug;
        switch ((level ?? "").Trim().ToLowerInvariant())
        {
            case "info":    min = Serilog.Events.LogEventLevel.Information; break;
            case "warn":
            case "warning": min = Serilog.Events.LogEventLevel.Warning; break;
            case "error":   min = Serilog.Events.LogEventLevel.Error; break;
            case "debug":
            default:        min = Serilog.Events.LogEventLevel.Debug; break;
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
        
        Log.Information("startup v={Version} rid={Rid} pid={Pid} cwd={Cwd}",
            typeof(Program).Assembly.GetName().Version,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            Environment.ProcessId,
            Environment.CurrentDirectory);

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
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver  = string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() ?? "0.0.0" : info;

        string rid;
        try { rid = RuntimeInformation.RuntimeIdentifier; }
        catch { rid = $"{Environment.OSVersion.Platform}-{RuntimeInformation.OSArchitecture}".ToLowerInvariant(); }

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
        Console.WriteLine("  --engine <auto|libvlc|mpv|ffplay>");
        Console.WriteLine("  --theme <base|accent|native|auto>");
        Console.WriteLine("  --feed <all|saved|downloaded|history|queue|GUID>");
        Console.WriteLine("  --search \"<term>\"");
        Console.WriteLine("  --opml-import <FILE> [--import-mode merge|replace|dry-run]");
        Console.WriteLine("  --opml-export <FILE>");
        Console.WriteLine("  --offline");
        Console.WriteLine("  --ascii");
        Console.WriteLine("  --log-level <debug|info|warn|error>");
    }

    // ===== Bridge: AppFacade <-> AppData =====
    static class Bridge
    {
        public static void SyncFromFacadeToAppData(AppFacade app, AppData data)
        {
            // Config → AppData
            data.PreferredEngine = app.EnginePreference;
            data.Volume0_100     = app.Volume0_100;
            data.Speed           = app.Speed;
            data.ThemePref       = app.Theme;
            data.PlayerAtTop     = app.PlayerAtTop;
            data.UnplayedOnly    = app.UnplayedOnly;
            data.SortBy          = app.SortBy;
            data.SortDir         = app.SortDir;
            data.PlaySource      = data.PlaySource ?? "auto";

            // Inhalte
            data.Feeds.Clear();    data.Feeds.AddRange(app.Feeds);
            data.Episodes.Clear(); data.Episodes.AddRange(app.Episodes);
            data.Queue.Clear();    data.Queue.AddRange(app.Queue);

            // Network-Flag bleibt Laufzeitflag in AppData
        }

        public static void SyncFromAppDataToFacade(AppData data, AppFacade app)
        {
            // Config
            app.EnginePreference = data.PreferredEngine;
            app.Volume0_100      = data.Volume0_100;
            app.Speed            = data.Speed;
            app.Theme            = data.ThemePref ?? app.Theme;
            app.PlayerAtTop      = data.PlayerAtTop;
            app.UnplayedOnly     = data.UnplayedOnly;
            app.SortBy           = data.SortBy  ?? app.SortBy;
            app.SortDir          = data.SortDir ?? app.SortDir;

            // Inhalte (nur solche, die im laufenden Betrieb verändert werden)
            // Queue (UI arbeitet über Data.Queue)
            // – hier schreiben wir einmaliger Snapshot zurück:
            foreach (var q in app.Queue.ToList()) app.QueueRemove(q);
            foreach (var id in data.Queue) app.QueuePush(id);

            // Saved / Progress / History werden durch Coordinator/Router via LibraryStore aktualisiert.
        }
    }

    // Adapter, um den DownloadManager als reines ReadModel in die Façade einzuhängen
    // Adapter, um den DownloadManager als reines ReadModel in die Façade einzuhängen
    sealed class DownloadLookupAdapter : AppFacade.ILocalDownloadLookup
    {
        private readonly DownloadManager _mgr;
        private readonly AppData _data;

        public DownloadLookupAdapter(DownloadManager mgr, AppData data)
        {
            _mgr = mgr;
            _data = data;
        }

        public bool IsDownloaded(Guid episodeId)
            => TryGetLocalPath(episodeId, out _);

        public bool TryGetLocalPath(Guid episodeId, out string? path)
        {
            path = null;
            if (_data.DownloadMap.TryGetValue(episodeId, out var st) &&
                st.State == DownloadState.Done &&
                !string.IsNullOrWhiteSpace(st.LocalPath) &&
                File.Exists(st.LocalPath))
            {
                path = st.LocalPath;
                return true;
            }
            return false;
        }
    }

}
