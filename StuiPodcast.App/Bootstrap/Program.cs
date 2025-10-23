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
using StuiPodcast.App.Command.Handler;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Player;
using StuiPodcast.Infra.Opml;
using ThemeMode = StuiPodcast.App.UI.UiShell.ThemeMode;
using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Command;
using StuiPodcast.App.Services;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;

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

    static SwappableAudioPlayer?  _player;
    static PlaybackCoordinator? _playback;
    static MemoryLogSink     _memLog = new(2000);
    static DownloadManager?  _downloader;
    
    

    static UiShell?            _ui;

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
        var cli = CliEntrypoint.Parse(args);

        if (cli.ShowVersion) { PrintVersion(); return; }
        if (cli.ShowHelp)    { PrintHelp();    return; }

        WinConsoleUtil.Enable();
        if (cli.Ascii) { try { GlyphSet.Use(GlyphSet.Profile.Ascii); } catch { } }

        LoggerSetup.Configure(cli.LogLevel, _memLog);
        CmdErrorHandlers.Install();

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

        AppBridge.SyncFromFacadeToAppData(_app, _data);

        // CLI: Engine-Präferenz vor AudioPlayer-Erzeugung übernehmen
        if (!string.IsNullOrWhiteSpace(cli.Engine))
            _data.PreferredEngine = cli.Engine!.Trim().ToLowerInvariant();

        // ---- AudioPlayer / Engine service ----
        _engineSvc = new EngineService(_data, _memLog);
        _player = _engineSvc.Create(out var engineInfo);

        // ---- Coordinator, Feeds, Saver ----
        _saver   = new SaveScheduler(_data, _app, () => AppBridge.SyncFromAppDataToFacade(_data, _app));
        _playback = new PlaybackCoordinator(_data, _player!, _saver.RequestSaveAsync, _memLog);
        _feeds    = new FeedService(_data, _app);

        Log.Information("cfg at {Cfg}", _configStore.FilePath);
        Log.Information("lib at {Lib}", _libraryStore.FilePath);

        // ---- Apply AudioPlayer Prefs ----
        _engineSvc.ApplyPrefsTo(_player);

        // ---- UI init ----
        Application.Init();
        _ui = new UiShell(_memLog);
        try { _data.LastSelectedFeedId = _ui.AllFeedId; } catch { }
        _ui.Build();
        UiComposer.UpdateWindowTitleWithDownloads(_ui, _data);
        
        UiComposer.ScrollAllToTopOnIdle(_ui, _data);

        // ---- Theme resolve (Default = User) ----
        var themeChoice = UiThemeResolver.Resolve(cli.Theme, _data.ThemePref);
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
            audioPlayer: _player!,
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
        CmdApplier.ApplyPostUiFlags(
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
