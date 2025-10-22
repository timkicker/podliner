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

// ==========================================================
//  Program.cs — refactored into multiple classes (same file)
//  - Program: composition root
//  - Cli: parse args
//  - WindowsConsoleUtil: enable VT/UTF-8 on Windows
//  - LoggerSetup: configure Serilog
//  - ErrorHandlers: global exception handlers
//  - ThemeResolver: resolves effective theme (default=user)
//  - SaveScheduler: throttled, centralized persistence
//  - NetworkMonitor: connectivity probing + hysteresis
//  - EngineService: create/swap audio engines and apply prefs
//  - UiComposer: build Shell, wire events & behaviors
//  - CommandApplier: apply post-UI CLI flags
//  - Bridge: AppFacade <-> AppData sync
//  - DownloadLookupAdapter: bridge downloads to AppFacade
// ==========================================================

class Program
{
    // ===== Runtime Flags =====
    internal static bool SkipSaveOnExit = false;

    // ===== Singletons / Runtime state =====
    static AppFacade?        _app;            // Persistenz-Fassade
    static ConfigStore?      _configStore;    // appsettings.json
    static LibraryStore?     _libraryStore;   // library/library.json
    static AppData           _data = new();   // UI-Laufzeitstate
    static FeedService?      _feeds;

    static SwappablePlayer?  _player;
    static PlaybackCoordinator? _playback;
    static MemoryLogSink     _memLog = new(2000);
    static DownloadManager?  _downloader;
    
    

    static Shell?            _ui;

    // Services
    static SaveScheduler?    _saver;
    static NetworkMonitor?   _net;
    static EngineService?    _engineSvc;

    // Timers
    static object? _uiTimer;
    static object? _netTimerToken;

    // Exit guard
    static int _exitOnce = 0;

    // ===== Entry =====
    static async Task Main(string[]? args)
    {
        var cli = Cli.Parse(args);

        if (cli.ShowVersion) { PrintVersion(); return; }
        if (cli.ShowHelp)    { PrintHelp();    return; }

        WindowsConsoleUtil.Enable();
        if (cli.Ascii) { try { GlyphSet.Use(GlyphSet.Profile.Ascii); } catch { } }

        LoggerSetup.Configure(cli.LogLevel, _memLog);
        ErrorHandlers.Install();

        // ---- Bootstrap: paths, stores, facade, downloader ----
        var appConfigDir = ResolveConfigDir();
        _configStore     = new ConfigStore(appConfigDir);
        _libraryStore    = new LibraryStore(appConfigDir, subFolder: "", fileName: "library.json");

        Console.WriteLine($"Config:  {_configStore.FilePath}");
        Console.WriteLine($"Library: {_libraryStore.FilePath}");

        _downloader = new DownloadManager(_data, appConfigDir);
        var downloadLookup = new DownloadLookupAdapter(_downloader, _data);

        _app = new AppFacade(_configStore, _libraryStore, downloadLookup);

        // ---- load & bridge to AppData ----
        var cfg = _app.LoadConfig();
        var lib = _app.LoadLibrary();
        Log.Information("loaded config theme={Theme} playerAtTop={PlayerTop} sort={SortBy}/{SortDir}",
            cfg.Theme, cfg.Ui.PlayerAtTop, cfg.ViewDefaults.SortBy, cfg.ViewDefaults.SortDir);
        Log.Information("loaded library feeds={FeedCount} episodes={EpCount} queue={QCount} history={HCount}",
            lib.Feeds.Count, lib.Episodes.Count, lib.Queue?.Count ?? 0, lib.History?.Count ?? 0);

        Bridge.SyncFromFacadeToAppData(_app, _data);

        // CLI: Engine-Präferenz vor Player-Erzeugung übernehmen
        if (!string.IsNullOrWhiteSpace(cli.Engine))
            _data.PreferredEngine = cli.Engine!.Trim().ToLowerInvariant();

        // ---- Player / Engine service ----
        _engineSvc = new EngineService(_data, _memLog);
        _player = _engineSvc.Create(out var engineInfo);

        // ---- Coordinator, Feeds, Saver ----
        _saver   = new SaveScheduler(_data, _app, () => Bridge.SyncFromAppDataToFacade(_data, _app));
        _playback = new PlaybackCoordinator(_data, _player!, _saver.RequestSaveAsync, _memLog);
        _feeds    = new FeedService(_data, _app);

        Log.Information("cfg at {Cfg}", _configStore.FilePath);
        Log.Information("lib at {Lib}", _libraryStore.FilePath);

        // ---- Apply Player Prefs ----
        _engineSvc.ApplyPrefsTo(_player);

        // ---- UI init ----
        Application.Init();
        _ui = new Shell(_memLog);
        try { _data.LastSelectedFeedId = _ui.AllFeedId; } catch { }
        _ui.Build();
        UiComposer.UpdateWindowTitleWithDownloads(_ui, _data);
        
        UiComposer.ScrollAllToTopOnIdle(_ui, _data);

        // ---- Theme resolve (Default = User) ----
        var themeChoice = ThemeResolver.Resolve(cli.Theme, _data.ThemePref);
        try
        {
            _ui.SetTheme(themeChoice.Mode);
            if (themeChoice.ShouldPersistPref != null)
            {
                _data.ThemePref = themeChoice.ShouldPersistPref;
                await _saver.RequestSaveAsync();
            }
        } catch { }

        // ---- Network monitor ----
        _net = new NetworkMonitor(_data, _ui, _saver.RequestSaveAsync);
        _net.Start(out _netTimerToken);

        // ---- Wire UI behaviors (sorter, lookups, events) ----
        _ui.EpisodeSorter = eps => UiComposer.ApplySort(eps, _data);
        _ui.SetUnplayedHint(_data.UnplayedOnly);
        _ui.SetPlayerPlacement(_data.PlayerAtTop);

        // Lookups
        _ui.SetQueueLookup(id => _data.Queue.Contains(id));
        _ui.SetDownloadStateLookup(id => _app!.IsDownloaded(id) ? DownloadState.Done : DownloadState.None);
        _ui.SetOfflineLookup(() => !_data.NetworkOnline);

        // Theme changes
        _ui.ThemeChanged += mode =>
        {
            _data.ThemePref = mode.ToString();
            _ = _saver!.RequestSaveAsync();
        };

        // Downloader → UI
        UiComposer.AttachDownloaderUi(_downloader!, _ui, _data);

        // Build remaining UI behaviors
        UiComposer.WireUi(
            ui: _ui,
            data: _data,
            app: _app!,
            feeds: _feeds!,
            playback: _playback!,
            player: _player!,
            save: _saver.RequestSaveAsync,
            engineSwitch: pref => _engineSvc!.SwitchAsync(_player!, pref, _saver!.RequestSaveAsync),
            updateTitle: () => UiComposer.UpdateWindowTitleWithDownloads(_ui!, _data),
            hasFeedWithUrl: HasFeedWithUrl
        );

        // Progress persistence tick (UI timer)
        _uiTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            try
            {
                if (_ui != null && _player != null && _playback != null)
                {
                    _playback.PersistProgressTick(
                        _player.State,
                        eps => {
                            var fid = _ui.GetSelectedFeedId();
                            if (fid != null) _ui.SetEpisodesForFeed(fid.Value, eps);
                        },
                        _data.Episodes);
                }
            }
            catch { }
            return true;
        });

        // ---------- APPLY CLI FLAGS (post-UI) ----------
        CommandApplier.ApplyPostUiFlags(
            cli, _ui, _data, _player!, _playback!, _memLog, _saver.RequestSaveAsync, _downloader!, pref => _engineSvc!.SwitchAsync(_player!, pref, _saver!.RequestSaveAsync));

        // Initial lists
        UiComposer.ShowInitialLists(_ui, _data);

        try { Application.Run(); }
        finally
        {
            Log.Information("shutdown begin");
            try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }
            try { if (_netTimerToken is not null) Application.MainLoop.RemoveTimeout(_netTimerToken); } catch {}

            try { _player?.Stop(); } catch { }
            (_player as IDisposable)?.Dispose();

            try { _downloader?.Dispose(); } catch { }

            if (!SkipSaveOnExit) { await _saver!.RequestSaveAsync(flush:true); }

            try { Application.Shutdown(); } catch { }
            TerminalUtil.ResetHard();
            try { Log.CloseAndFlush(); } catch { }
            Log.Information("shutdown end");
        }
    }
    
    // in class Program (z. B. direkt unter ResolveConfigDir)
    // --- kleine Helper für andere Klassen ---
    internal static bool MarkExiting() => Interlocked.Exchange(ref _exitOnce, 1) == 1;

    internal static object? NetTimerToken => _netTimerToken;
    internal static object? UiTimerToken  => _uiTimer;
    internal static DownloadManager? DownloaderInstance => _downloader;
    internal static MemoryLogSink MemLogSinkInstance => _memLog;

// Für Fremdcode, der das alte API erwartet:
    public static bool IsDownloaded(Guid episodeId) => _app?.IsDownloaded(episodeId) ?? false;
    public static bool TryGetLocalPath(Guid episodeId, out string? path)
    {
        if (_app != null && _app.TryGetLocalPath(episodeId, out path)) return true;
        path = null;
        return false;
    }



    static string ResolveConfigDir()
    {
        var baseConfigDir =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)                 // %APPDATA%
                : (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")); // ~/.config
        return Path.Combine(baseConfigDir, "podliner");
    }

    static bool HasFeedWithUrl(string url)
    {
        return _data.Feeds.Any(f =>
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

    static void PrintVersion()
    {
        var asm  = typeof(Program).Assembly;
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
        Console.WriteLine("  --theme <base|accent|native|auto|user>");
        Console.WriteLine("  --feed <all|saved|downloaded|history|queue|GUID>");
        Console.WriteLine("  --search \"<term>\"");
        Console.WriteLine("  --opml-import <FILE> [--import-mode merge|replace|dry-run]");
        Console.WriteLine("  --opml-export <FILE>");
        Console.WriteLine("  --offline");
        Console.WriteLine("  --ascii");
        Console.WriteLine("  --log-level <debug|info|warn|error>");
    }
}

// ==========================================================
// CLI
// ==========================================================
sealed class Cli
{
    internal sealed class Options
    {
        public string? Engine;
        public string? Theme;
        public string? Feed;
        public string? Search;
        public string? OpmlImport;
        public string? OpmlImportMode;
        public string? OpmlExport;
        public bool Offline;
        public bool Ascii;
        public string? LogLevel;
        public bool ShowVersion;
        public bool ShowHelp;
    }

    public static Options Parse(string[]? args)
    {
        var o = new Options();
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
}

// ==========================================================
// Windows console VT/UTF-8
// ==========================================================
static class WindowsConsoleUtil
{
    public static void Enable()
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
}

// ==========================================================
// Logger setup
// ==========================================================
static class LoggerSetup
{
    public static void Configure(string? level, MemoryLogSink memLog)
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
            .WriteTo.Sink(memLog)
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
}

// ==========================================================
// Error handlers
// ==========================================================
static class ErrorHandlers
{
    public static void Install()
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
}

// ==========================================================
// Theme resolving (default = User)
// ==========================================================
static class ThemeResolver
{
    public sealed record Result(ThemeMode Mode, string? ShouldPersistPref);

    public static Result Resolve(string? cliTheme, string? savedPref)
    {
        if (!string.IsNullOrWhiteSpace(cliTheme))
        {
            var t = cliTheme.Trim().ToLowerInvariant();
            var cliAskedAuto = t == "auto";

            ThemeMode tm = t switch
            {
                "base"   => ThemeMode.Base,
                "accent" => ThemeMode.MenuAccent,
                "native" => ThemeMode.Native,
                "user"   => ThemeMode.User,
                "auto"   => ThemeMode.User, // default → user
                _        => ThemeMode.User
            };
            return new Result(tm, cliAskedAuto ? "auto" : tm.ToString());
        }

        var pref = (savedPref ?? "auto").Trim();
        ThemeMode desired =
            pref.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? ThemeMode.User
                : (Enum.TryParse<ThemeMode>(pref, out ThemeMode saved) ? saved : ThemeMode.User);

        // Keep "auto" string if it was saved
        return new Result(desired, null);
    }
}

// ==========================================================
// Save scheduler (centralized persistence)
// ==========================================================
sealed class SaveScheduler : IDisposable
{
    readonly AppData _data;
    readonly AppFacade _app;
    readonly Action _syncFromDataToFacade;

    readonly object _gate = new();
    DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    bool _pending, _running;
    const int MIN_INTERVAL_MS = 1000;

    public SaveScheduler(AppData data, AppFacade app, Action syncFromDataToFacade)
    {
        _data = data;
        _app = app;
        _syncFromDataToFacade = syncFromDataToFacade;
    }

    // in SaveScheduler
    public Task RequestSaveAsync() => RequestSaveAsync(flush: false);

    
    public async Task RequestSaveAsync(bool flush = false)
    {
        if (flush)
        {
            await SaveNowAsync().ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var since = now - _lastSave;

            if (!_running && since.TotalMilliseconds >= MIN_INTERVAL_MS)
            {
                _running = true;
            }
            else
            {
                ScheduleDelayed();
                return;
            }
        }

        await SaveNowAsync().ConfigureAwait(false);
    }

    void ScheduleDelayed()
    {
        if (_pending) return;
        _pending = true;

        _ = Task.Run(async () =>
        {
            await Task.Delay(MIN_INTERVAL_MS).ConfigureAwait(false);
            lock (_gate)
            {
                _pending = false;
                if (_running) return;
                _running = true;
            }
            _ = SaveNowAsync();
        });
    }

    async Task SaveNowAsync()
    {
        try
        {
            _syncFromDataToFacade();
            _app.SaveNow();
        }
        catch (Exception ex) { Log.Debug(ex, "save failed"); }
        finally
        {
            lock (_gate)
            {
                _lastSave = DateTimeOffset.Now;
                _running = false;
            }
        }
        await Task.CompletedTask;
    }

    public void Dispose() { /* nothing */ }
}

// ==========================================================
// Network monitor (probing + hysteresis)
// ==========================================================
sealed class NetworkMonitor
{
    readonly AppData _data;
    readonly Shell _ui;
    readonly Func<Task> _saveAsync;

    static readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

    volatile bool _probeRunning = false;
    int _ok = 0, _fail = 0;
    const int FAILS_FOR_OFFLINE = 4;
    const int SUCC_FOR_ONLINE   = 3;

    DateTimeOffset _lastFlip = DateTimeOffset.MinValue;
    static readonly TimeSpan _minDwell = TimeSpan.FromSeconds(15);

    DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    static readonly TimeSpan _heartbeatEvery = TimeSpan.FromMinutes(2);

    public NetworkMonitor(AppData data, Shell ui, Func<Task> saveAsync)
    {
        _data = data;
        _ui = ui;
        _saveAsync = saveAsync;
    }

    public void Start(out object? timerToken)
    {
        // first quick probe
        _ = Task.Run(async () =>
        {
            bool online = await QuickNetCheckAsync();
            _ok   = online ? 1 : 0;
            _fail = online ? 0 : 1;
            _lastFlip = DateTimeOffset.UtcNow;
            OnNetworkChanged(online);
        });

        try
        {
            NetworkChange.NetworkAvailabilityChanged += (s, e) => { TriggerProbe(); };
        } catch { }

        timerToken = Application.MainLoop.AddTimeout(NetProbeInterval(), _ =>
        {
            TriggerProbe();
            Application.MainLoop.AddTimeout(NetProbeInterval(), __ =>
            {
                TriggerProbe();
                return true;
            });
            return false;
        });
    }

    public static TimeSpan NetProbeInterval(AppData? data = null)
        => (data?.NetworkOnline ?? true) ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(5);

    void TriggerProbe()
    {
        if (_probeRunning) return;
        _probeRunning = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var probeOnline = await QuickNetCheckAsync().ConfigureAwait(false);

                if (probeOnline) { _ok++;  _fail = 0; }
                else             { _fail++; _ok   = 0; }

                bool state   = _data.NetworkOnline;
                bool dwellOk = (DateTimeOffset.UtcNow - _lastFlip) >= _minDwell;

                bool flipToOn  = !state && probeOnline && _ok   >= SUCC_FOR_ONLINE && dwellOk;
                bool flipToOff =  state && !probeOnline && _fail >= FAILS_FOR_OFFLINE && dwellOk;

                Log.Information("net/decision prev={Prev} probe={Probe} ok={Ok} fail={Fail} dwellOk={DwellOk} flipOn={FlipOn} flipOff={FlipOff}",
                    state ? "online" : "offline",
                    probeOnline ? "online" : "offline",
                    _ok, _fail, dwellOk,
                    flipToOn, flipToOff);

                if (flipToOn || flipToOff)
                {
                    _lastFlip = DateTimeOffset.UtcNow;
                    LogNicsSnapshot();
                    Log.Information("net/state change → {State}", flipToOn ? "online" : "offline");
                    OnNetworkChanged(flipToOn);
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastHeartbeat >= _heartbeatEvery)
                    {
                        _lastHeartbeat = now;
                        Log.Debug("net/steady state={State} ok={Ok} fail={Fail}",
                            _data.NetworkOnline ? "online" : "offline", _ok, _fail);
                    }
                }
            }
            finally { _probeRunning = false; }
        });
    }

    void OnNetworkChanged(bool online)
    {
        _data.NetworkOnline = online;

        Application.MainLoop?.Invoke(() =>
        {
            if (_ui == null) return;

            CommandRouter.ApplyList(_ui, _data);
            _ui.RefreshEpisodesForSelectedFeed(_data.Episodes);

            var nowId = _ui.GetNowPlayingId();
            if (nowId != null)
            {
                var ep = _data.Episodes.FirstOrDefault(x => x.Id == nowId);
                if (ep != null)
                    _ui.SetWindowTitle((!_data.NetworkOnline ? "[OFFLINE] " : "") + (ep.Title ?? "—"));
            }

            if (!online) _ui.ShowOsd("net: offline", 800);
        });

        _ = _saveAsync();
    }

    static async Task<bool> QuickNetCheckAsync()
    {
        bool anyUp = false;
        try { anyUp = NetworkInterface.GetIsNetworkAvailable(); Log.Verbose("net/probe nics-available={Avail}", anyUp); } catch { }

        var tcpOk =
            await TcpCheckAsync("1.1.1.1", 443, 900).ConfigureAwait(false) ||
            await TcpCheckAsync("8.8.8.8", 53, 900).ConfigureAwait(false);

        var httpOk =
            await HttpProbeAsync("http://connectivitycheck.gstatic.com/generate_204").ConfigureAwait(false) ||
            await HttpProbeAsync("http://www.msftconnecttest.com/connecttest.txt").ConfigureAwait(false);

        Log.Verbose("net/probe result tcp={TcpOk} http={HttpOk} anyNicUp={NicUp}",
            tcpOk, httpOk, anyUp);

        return (tcpOk || httpOk) && anyUp;
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
            await sock.ConnectAsync(hostOrIp, port, cts.Token).ConfigureAwait(false);
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
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _probeHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            Log.Verbose("net/probe http {Url} {Code}", url, (int)resp.StatusCode);
            return ((int)resp.StatusCode) is >= 200 and < 400;
        }
        catch (Exception exHead)
        {
            try
            {
                using var resp = await _probeHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                Log.Verbose("net/probe http[GET] {Url} {Code} (HEAD failed: {Err})", url, (int)resp.StatusCode, exHead.GetType().Name);
                return ((int)resp.StatusCode) is >= 200 and < 400;
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "net/probe http fail {Url}", url);
                return false;
            }
        }
    }

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
}

// ==========================================================
// Engine service (create/switch/apply prefs)
// ==========================================================
sealed class EngineService
{
    readonly AppData _data;
    readonly MemoryLogSink _memLog;
    public SwappablePlayer? Current { get; private set; }
    string? _initialInfo;

    
    // in class EngineService
    public void ApplyPrefsToCurrent(SwappablePlayer sp)
    {
        try {
            var v = Math.Clamp(_data.Volume0_100, 0, 100);
            if (v != 0 || _data.Volume0_100 == 0) sp.SetVolume(v);
        } catch {}

        try {
            var s = _data.Speed; if (s <= 0) s = 1.0;
            sp.SetSpeed(Math.Clamp(s, 0.25, 3.0));
        } catch {}
    }

    
    public EngineService(AppData data, MemoryLogSink memLog)
    {
        _data = data;
        _memLog = memLog;
    }

    public SwappablePlayer Create(out string engineInfo)
    {
        var core = PlayerFactory.Create(_data, out var info);
        engineInfo = info;
        _initialInfo = info;
        Current = new SwappablePlayer(core);
        return Current;
    }

    public void ApplyPrefsTo(IPlayer p)
    {
        try
        {
            if ((p.Capabilities & PlayerCapabilities.Volume) != 0)
            {
                var v = Math.Clamp(_data.Volume0_100, 0, 100);
                p.SetVolume(v);
            }
        } catch { }

        try
        {
            if ((p.Capabilities & PlayerCapabilities.Speed) != 0)
            {
                var s = _data.Speed;
                if (s <= 0) s = 1.0;
                p.SetSpeed(Math.Clamp(s, 0.25, 3.0));
            }
        } catch { }
    }

    public async Task SwitchAsync(SwappablePlayer player, string pref, Func<Task> onPersistTick)
    {
        try
        {
            _data.PreferredEngine = string.IsNullOrWhiteSpace(pref) ? "auto" : pref.Trim().ToLowerInvariant();
            _ = onPersistTick();

            var next = PlayerFactory.Create(_data, out var info);
            Log.Information("engine created name={Engine} caps={Caps} info={Info}",
                player?.Name, (player?.Capabilities).ToString(), _initialInfo);
            ApplyPrefsTo(next);

            await player.SwapToAsync(next, old => { try { old.Stop(); } catch { } });
            Log.Information("engine switched current={Name} caps={Caps}", player.Name, player.Capabilities);
            // OSD is raised by callers (UI)
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "engine switch failed");
        }
    }
}

// ==========================================================
// UI composition & behaviors
// ==========================================================
static class UiComposer
{
    
    public static void ScrollAllToTopOnIdle(Shell ui, AppData data)
    {
        // Startfeed („All“) als initiale Selektion sicherstellen + in Sichtweite bringen
        Application.MainLoop?.AddIdle(() =>
        {
            try
            {
                data.LastSelectedFeedId = ui.AllFeedId;
                ui.EnsureSelectedFeedVisibleAndTop();
            } catch { }
            return false;
        });

        // Episoden für „All“ setzen und Liste nach oben + Fokus
        Application.MainLoop?.AddIdle(() =>
        {
            try
            {
                ui.SetEpisodesForFeed(ui.AllFeedId, data.Episodes);
                Application.MainLoop!.AddIdle(() =>
                {
                    try { ui.ScrollEpisodesToTopAndFocus(); } catch { }
                    return false;
                });
            } catch { }
            return false;
        });
    }

    
    public static void UpdateWindowTitleWithDownloads(Shell ui, AppData data)
    {
        var offlinePrefix = (!data.NetworkOnline) ? "[OFFLINE] " : "";
        string baseTitle = "Podliner";
        try
        {
            var nowId = ui.GetNowPlayingId();
            if (nowId != null)
            {
                var ep = data.Episodes.FirstOrDefault(x => x.Id == nowId);
                if (ep != null && !string.IsNullOrWhiteSpace(ep.Title))
                    baseTitle = ep.Title!;
            }
        }
        catch { }
        ui.SetWindowTitle($"{offlinePrefix}{baseTitle}");
    }

    public static IEnumerable<Episode> ApplySort(IEnumerable<Episode> eps, AppData data)
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
            if (len <= 60_000) return (pos >= (long)(len * 0.98)) || (len - pos <= 500);
            return (pos >= (long)(len * 0.995)) || (len - pos <= 2000);
        }

        string FeedTitle(Episode e)
        {
            var f = ProgramData().Feeds.FirstOrDefault(x => x.Id == e.FeedId);
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

        // local accessor to avoid passing data around in the sort key
        AppData ProgramData() => data;
    }

    public static void AttachDownloaderUi(DownloadManager downloader, Shell ui, AppData data)
    {
        DateTime _dlLastUiPulse = DateTime.MinValue;

        downloader.StatusChanged += (id, st) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                ui?.RefreshEpisodesForSelectedFeed(data.Episodes);
                UpdateWindowTitleWithDownloads(ui, data);
                if (ui != null)
                {
                    switch (st.State)
                    {
                        case DownloadState.Queued:     ui.ShowOsd("⌵", 300); break;
                        case DownloadState.Running:    ui.ShowOsd("⇣", 300); break;
                        case DownloadState.Verifying:  ui.ShowOsd("≈", 300); break;
                        case DownloadState.Done:       ui.ShowOsd("⬇", 500); break;
                        case DownloadState.Failed:     ui.ShowOsd("!", 900);  break;
                        case DownloadState.Canceled:   ui.ShowOsd("×", 400);  break;
                    }
                }
            });

            if (st.State == DownloadState.Running && (DateTime.UtcNow - _dlLastUiPulse) > TimeSpan.FromMilliseconds(500))
            {
                _dlLastUiPulse = DateTime.UtcNow;
                Application.MainLoop?.Invoke(() => ui?.RefreshEpisodesForSelectedFeed(data.Episodes));
            }
        };

        downloader.EnsureRunning();
    }

    public static void WireUi(
        Shell ui,
        AppData data,
        AppFacade app,
        FeedService feeds,
        PlaybackCoordinator playback,
        SwappablePlayer player,
        Func<Task> save,
        Func<string, Task> engineSwitch,
        Action updateTitle,
        Func<string, bool> hasFeedWithUrl)
    {
        // Quit
        ui.QuitRequested += () => QuitApp(ui, player, feeds, save);

        // Add Feed
        ui.AddFeedRequested += async url =>
        {
            if (ui == null || feeds == null) return;
            if (string.IsNullOrWhiteSpace(url)) { ui.ShowOsd("Add feed: URL fehlt", 1500); return; }

            Log.Information("ui/addfeed url={Url}", url);
            ui.ShowOsd("Adding feed…", 800);

            try
            {
                if (hasFeedWithUrl(url)) { ui.ShowOsd("Already added", 1200); return; }
            }
            catch { }

            try
            {
                var f = await feeds.AddFeedAsync(url);
                app?.SaveNow();  // sofort in library.json schreiben
                Log.Information("ui/addfeed ok id={Id} title={Title}", f.Id, f.Title);

                data.LastSelectedFeedId = f.Id;
                _ = save();

                ui.SetFeeds(data.Feeds, f.Id);
                ui.SetEpisodesForFeed(f.Id, data.Episodes);
                ui.SelectEpisodeIndex(0);

                ui.ShowOsd("Feed added ✓", 1200);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ui/addfeed failed url={Url}", url);
                ui.ShowOsd($"Add failed: {ex.Message}", 2200);
            }
        };

        // Refresh
        ui.RefreshRequested += async () =>
        {
            await feeds!.RefreshAllAsync();

            var selected = ui.GetSelectedFeedId() ?? data.LastSelectedFeedId;
            ui.SetFeeds(data.Feeds, selected);

            if (selected != null)
            {
                ui.SetEpisodesForFeed(selected.Value, data.Episodes);
            }
            CommandRouter.ApplyList(ui, data);
        };

        // Selection changed
        ui.SelectedFeedChanged += () =>
        {
            var fid = ui.GetSelectedFeedId();
            data.LastSelectedFeedId = fid;

            if (fid != null)
            {
                ui.SetEpisodesForFeed(fid.Value, data.Episodes);
                ui.SelectEpisodeIndex(0);
            }

            _ = save();
        };

        // EpisodeSelectionChanged (Index merken optional)
        ui.EpisodeSelectionChanged += () => { _ = save(); };

        // Play selected
        ui.PlaySelected += () =>
        {
            var ep = ui?.GetSelectedEpisode();
            if (ep == null || player == null || playback == null || ui == null) return;

            var curFeed = ui.GetSelectedFeedId();

            ep.Progress.LastPlayedAt = DateTimeOffset.UtcNow;
            _ = save();

            if (curFeed is Guid fid && fid == ui.QueueFeedId)
            {
                int ix = data.Queue.FindIndex(id => id == ep.Id);
                if (ix >= 0)
                {
                    data.Queue.RemoveRange(0, ix + 1);
                    ui.SetQueueOrder(data.Queue);
                    ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                    _ = save();
                }
            }

            string? localPath = null;
            if (app!.TryGetLocalPath(ep.Id, out var lp)) localPath = lp;

            bool isRemote =
                string.IsNullOrWhiteSpace(localPath) &&
                !string.IsNullOrWhiteSpace(ep.AudioUrl) &&
                ep.AudioUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

            var baseline = TimeSpan.Zero;
            try { baseline = player?.State.Position ?? TimeSpan.Zero; } catch { }
            ui.SetPlayerLoading(true, isRemote ? "Loading…" : "Opening…", baseline);

            var mode   = (data.PlaySource ?? "auto").Trim().ToLowerInvariant();
            var online = data.NetworkOnline;

            string? source = mode switch
            {
                "local"  => localPath,
                "remote" => ep.AudioUrl,
                _        => localPath ?? (online ? ep.AudioUrl : null)
            };

            if (string.IsNullOrWhiteSpace(source))
            {
                ui.SetPlayerLoading(false);
                var msg = (localPath == null) ? "∅ Offline: not downloaded" : "No playable source";
                ui.ShowOsd(msg, 1500);
                return;
            }

            var oldUrl = ep.AudioUrl;
            try
            {
                ep.AudioUrl = source;
                playback.Play(ep);
            }
            catch
            {
                ui.SetPlayerLoading(false);
                throw;
            }
            finally
            {
                ep.AudioUrl = oldUrl;
            }

            ui.SetWindowTitle((!data.NetworkOnline ? "[OFFLINE] " : "") + ep.Title);
            ui.SetNowPlaying(ep.Id);

            if (localPath != null)
            {
                Application.MainLoop?.AddTimeout(TimeSpan.FromMilliseconds(600), _ =>
                {
                    try
                    {
                        var s = player.State;
                        if (!s.IsPlaying && s.Position == TimeSpan.Zero)
                        {
                            try { player.Stop(); } catch { }
                            var fileUri = new Uri(localPath).AbsoluteUri;

                            var old = ep.AudioUrl;
                            try
                            {
                                ep.AudioUrl = fileUri;
                                playback.Play(ep);
                                ui.ShowOsd("Retry (file://)");
                            }
                            finally { ep.AudioUrl = old; }
                        }
                    }
                    catch { }
                    return false;
                });
            }
        };

        ui.ToggleThemeRequested += () => ui.ToggleTheme();

        // Manuell „gespielt“ toggeln
        ui.TogglePlayedRequested += () =>
        {
            var ep = ui?.GetSelectedEpisode();
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

            _ = save();

            var fid = ui.GetSelectedFeedId();
            if (fid != null) ui.SetEpisodesForFeed(fid.Value, data.Episodes);
            ui.ShowDetails(ep);
        };

        // Commands
        ui.Command += cmd =>
        {
            Log.Debug("cmd {Cmd}", cmd);
            if (ui == null || player == null || playback == null || Program.SkipSaveOnExit) { }

            if (CommandRouter.HandleQueue(cmd, ui, data, save))
            {
                ui.SetQueueOrder(data.Queue);
                ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                return;
            }

            if (CommandRouter.HandleDownloads(cmd, ui, data, ProgramDownloader(), save))
                return;

            CommandRouter.Handle(cmd, player, playback, ui, ProgramLog(), data, save, ProgramDownloader(), engineSwitch);
        };

        // Suche
        ui.SearchApplied += query =>
        {
            var fid = ui?.GetSelectedFeedId();
            IEnumerable<Episode> list = data.Episodes;
            if (fid != null) list = list.Where(e => e.FeedId == fid.Value);

            if (!string.IsNullOrWhiteSpace(query))
                list = list.Where(e =>
                    (e.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

            if (fid != null) ui?.SetEpisodesForFeed(fid.Value, list);
        };

        // Startup episode + initial lists are done outside (ShowInitialLists)
        static DownloadManager ProgramDownloader() => (DownloadManager)typeof(Program)
            .GetField("_downloader", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        static MemoryLogSink ProgramLog() => (MemoryLogSink)typeof(Program)
            .GetField("_memLog", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    }

    public static void ShowInitialLists(Shell ui, AppData data)
    {
        ui.SetFeeds(data.Feeds, data.LastSelectedFeedId);
        ui.SetUnplayedHint(data.UnplayedOnly);
        CommandRouter.ApplyList(ui, data);

        var initialFeed = ui.GetSelectedFeedId();
        if (initialFeed != null)
        {
            ui.SetEpisodesForFeed(initialFeed.Value, data.Episodes);
            ui.SelectEpisodeIndex(0);
        }

        var last = data.Episodes
            .OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Progress.LastPosMs)
            .FirstOrDefault()
            ?? ui.GetSelectedEpisode();

        if (last != null)
        {
            ui.SelectFeed(last.FeedId);
            ui.SetEpisodesForFeed(last.FeedId, data.Episodes);

            var list = data.Episodes
                .Where(e => e.FeedId == last.FeedId)
                .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                .ToList();

            var idx = Math.Max(0, list.FindIndex(e => e.Id == last.Id));
            ui.SelectEpisodeIndex(idx);

            ui.ShowStartupEpisode(last, data.Volume0_100, data.Speed);
        }

        // Player snapshot/status → UI (wiring here to keep UI composition encapsulated)
        var player = (SwappablePlayer)typeof(Program)
            .GetField("_player", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var playback = (PlaybackCoordinator)typeof(Program)
            .GetField("_playback", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        playback.SnapshotAvailable += snap => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui != null && player != null)
                {
                    ui.UpdatePlayerSnapshot(snap, player.State.Volume0_100);

                    var nowId = ui.GetNowPlayingId();
                    if (nowId is Guid nid && snap.EpisodeId == nid)
                    {
                        var ep = data.Episodes.FirstOrDefault(x => x.Id == nid);
                        if (ep != null)
                            ui.SetWindowTitle((!data.NetworkOnline ? "[OFFLINE] " : "") + (ep.Title ?? "—"));
                    }
                }
            }
            catch { }
        });

        playback.StatusChanged += st => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui == null) return;
                switch (st)
                {
                    case PlaybackStatus.Loading:
                        ui.SetPlayerLoading(true, "Loading…", null);
                        break;
                    case PlaybackStatus.SlowNetwork:
                        ui.SetPlayerLoading(true, "Connecting… (slow)", null);
                        break;
                    case PlaybackStatus.Playing:
                    case PlaybackStatus.Ended:
                    default:
                        ui.SetPlayerLoading(false);
                        break;
                }
            }
            catch { }
        });

        player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui != null && playback != null)
                {
                    playback.PersistProgressTick(
                        s,
                        eps => {
                            var fid = ui.GetSelectedFeedId();
                            if (fid != null) ui.SetEpisodesForFeed(fid.Value, eps);
                        },
                        data.Episodes);
                }
            }
            catch { }
        });
    }

    static void QuitApp(Shell ui, SwappablePlayer player, FeedService feeds, Func<Task> save)
    {
        // Schon im Exit? Dann nichts mehr tun.
        if (Program.MarkExiting()) return;

        // Timer sauber entfernen
        try { var t  = Program.NetTimerToken; if (t  is not null) Application.MainLoop?.RemoveTimeout(t); } catch { }
        try { var ut = Program.UiTimerToken;  if (ut is not null) Application.MainLoop?.RemoveTimeout(ut); } catch { }

        // Läufer stoppen
        try { Program.DownloaderInstance?.Stop(); } catch { }
        try { player?.Stop(); } catch { }

        // UI beenden
        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                try { Application.RequestStop(); } catch { }
            });
        }
        catch { }

        // Hard-Exit fallback
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            try { Environment.Exit(0); } catch { }
        });
    }

}

// ==========================================================
// Post-UI CLI flags applier
// ==========================================================
static class CommandApplier
{
    public static void ApplyPostUiFlags(
        Cli.Options cli,
        Shell ui,
        AppData data,
        SwappablePlayer player,
        PlaybackCoordinator playback,
        MemoryLogSink memLog,
        Func<Task> save,
        DownloadManager downloader,
        Func<string, Task> engineSwitch)
    {
        Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui == null || player == null || playback == null || downloader == null) return;

                if (cli.Offline)
                    CommandRouter.Handle(":net offline", player, playback, ui, memLog, data, save, downloader, engineSwitch);

                if (!string.IsNullOrWhiteSpace(cli.Engine))
                    CommandRouter.Handle($":engine {cli.Engine}", player, playback, ui, memLog, data, save, downloader, engineSwitch);

                if (!string.IsNullOrWhiteSpace(cli.OpmlExport))
                {
                    var path = cli.OpmlExport!;
                    Log.Information("cli/opml export path={Path}", path);
                    CommandRouter.Handle($":opml export {path}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                }

                if (!string.IsNullOrWhiteSpace(cli.OpmlImport))
                {
                    var mode = (cli.OpmlImportMode ?? "merge").Trim().ToLowerInvariant();

                    if (mode == "dry-run")
                    {
                        try
                        {
                            var xml = OpmlIo.ReadFile(cli.OpmlImport!);
                            var doc = OpmlParser.Parse(xml);
                            var plan = OpmlImportPlanner.Plan(doc, data.Feeds, updateTitles: false);
                            Log.Information("cli/opml dryrun path={Path} new={New} dup={Dup} invalid={Invalid}",
                                cli.OpmlImport, plan.NewCount, plan.DuplicateCount, plan.InvalidCount);
                            ui.ShowOsd($"OPML dry-run → new {plan.NewCount}, dup {plan.DuplicateCount}, invalid {plan.InvalidCount}", 2400);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "cli/opml dryrun failed path={Path}", cli.OpmlImport);
                            ui.ShowOsd($"OPML dry-run failed: {ex.Message}", 2000);
                        }
                    }
                    else
                    {
                        Log.Information("cli/opml import path={Path} mode={Mode}", cli.OpmlImport, mode);
                        if (mode == "replace")
                        {
                            Log.Information("cli/opml replace clearing existing feeds/episodes");
                            data.Feeds.Clear();
                            data.Episodes.Clear();
                            data.LastSelectedFeedId = ui.AllFeedId;
                            _ = save();

                            ui.SetFeeds(data.Feeds, data.LastSelectedFeedId);
                            CommandRouter.ApplyList(ui, data);
                        }

                        var path = cli.OpmlImport!.Contains(' ') ? $"\"{cli.OpmlImport}\"" : cli.OpmlImport!;
                        CommandRouter.Handle($":opml import {path}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                    }
                }

                if (!string.IsNullOrWhiteSpace(cli.Feed))
                {
                    var f = cli.Feed!.Trim();
                    CommandRouter.Handle($":feed {f}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                }

                if (!string.IsNullOrWhiteSpace(cli.Search))
                {
                    CommandRouter.Handle($":search {cli.Search}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                }
            }
            catch { }
        });
    }
}

// ==========================================================
// Bridge: AppFacade <-> AppData
// ==========================================================
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

        // Inhalte → Queue (Snapshot)
        foreach (var q in app.Queue.ToList()) app.QueueRemove(q);
        foreach (var id in data.Queue) app.QueuePush(id);
        // Saved / Progress / History werden anderweitig gepflegt (Coordinator/Router)
    }
}

// ==========================================================
// Adapter for download read-model into AppFacade
// ==========================================================
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
