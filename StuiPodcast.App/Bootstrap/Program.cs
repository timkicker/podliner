using System.Reflection;
using System.Runtime.InteropServices;
using Serilog;
using StuiPodcast.App.Command;
using StuiPodcast.App.Command.Handler;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Mpris;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;
using StuiPodcast.Infra.Sync;
using Terminal.Gui;

namespace StuiPodcast.App.Bootstrap;

internal class Program
{
    #region runtime flags
    // runtime flags
    internal static bool SkipSaveOnExit = false;
    #endregion

    #region singletons and runtime state
    // primary singletons / runtime state
    private static AppFacade?        _app;
    private static ConfigStore?      _configStore;
    private static LibraryStore?     _libraryStore;
    private static IEpisodeStore?    _episodes;
    private static IFeedStore?       _feedStore;
    private static IQueueService?    _queue;
    private static AppData           _data = new();
    private static FeedService?      _feeds;

    private static SwappableAudioPlayer?  _player;
    private static PlaybackCoordinator? _playback;
    private static MemoryLogSink     _memLog = new();
    private static DownloadManager?  _downloader;
    private static DownloadLookupAdapter? _downloadLookup;

    private static UiShell?            _ui;
    private static MprisService?       _mpris;
    private static GpodderStore?       _gpodderStore;
    private static GpodderSyncService? _gpodder;
    #endregion

    #region services
    // service objects
    private static SaveScheduler?    _saver;
    private static NetworkMonitor?   _net;
    private static EngineService?    _engineSvc;
    #endregion

    #region timers and guards
    // timers and exit guard
    private static object? _uiTimer;
    private static object? _netTimerToken;
    private static int _exitOnce;
    #endregion

    #region entry point
    // application entry
    private static async Task Main(string[]? args)
    {
        var cli = CliEntrypoint.Parse(args);

        if (cli.ShowVersion) { PrintVersion(); return; }
        if (cli.ShowHelp)    { PrintHelp();    return; }

        WinConsoleUtil.Enable();
        if (cli.Ascii) { try { UIGlyphSet.Use(UIGlyphSet.Profile.Ascii); }
            catch
            {
                // ignored
            }
        }

       
        LoggerSetup.Configure(cli.LogLevel, cli.LogDir, cli.NoFileLogs, _memLog);
        CmdErrorHandlers.Install();

        // bootstrap: paths, stores, facade, downloader
        var appConfigDir = ResolveConfigDir();
        _configStore     = new ConfigStore(appConfigDir);
        _libraryStore    = new LibraryStore(appConfigDir, subFolder: "", fileName: "library.json");

        Console.WriteLine($"Config:  {_configStore.FilePath}");
        Console.WriteLine($"Library: {_libraryStore.FilePath}");

        _downloader = new DownloadManager(_data, _libraryStore, appConfigDir);
        _downloadLookup = new DownloadLookupAdapter(_downloader, _data);

        _app = new AppFacade(_configStore, _libraryStore, _downloadLookup);

        // load and bridge to appdata
        var cfg = _app.LoadConfig();
        var lib = _app.LoadLibrary();
        Log.Information("loaded config theme={Theme} playerAtTop={PlayerTop} sort={SortBy}/{SortDir}",
            cfg.Theme, cfg.Ui.PlayerAtTop, cfg.ViewDefaults.SortBy, cfg.ViewDefaults.SortDir);
        Log.Information("loaded library feeds={FeedCount} episodes={EpCount} queue={QCount} history={HCount}",
            lib.Feeds.Count, lib.Episodes.Count, lib.Queue?.Count ?? 0, lib.History?.Count ?? 0);

        AppBridge.SyncFromFacadeToAppData(_app, _data);

        // Runtime stores are the new single source of truth for feed /
        // episode / queue reads. Built early so any later service can
        // receive them. Writes on legacy paths continue to update
        // LibraryStore directly, which the stores read through.
        _episodes  = new EpisodeStore(_libraryStore);
        _feedStore = new FeedStore(_libraryStore);
        _queue     = new QueueService(_libraryStore);

        // apply cli engine preference before creating audio player
        if (!string.IsNullOrWhiteSpace(cli.Engine))
            _data.PreferredEngine = cli.Engine!.Trim().ToLowerInvariant();

        // audio player / engine service
        _engineSvc = new EngineService(_data, _memLog);
        _player = _engineSvc.Create(out _);

        // coordinator, feeds, saver
        _saver   = new SaveScheduler(_data, _app, () => AppBridge.SyncFromAppDataToFacade(_data, _app));
        _playback = new PlaybackCoordinator(_data, _player, _saver.RequestSaveAsync, _memLog, _episodes, _queue);
        _feeds    = new FeedService(_data, _app, uiDispatch: DispatchToUi);

        // gpodder sync (opt-in; no-op if not configured)
        _gpodderStore = new GpodderStore(appConfigDir);
        _gpodderStore.Load();
        _gpodder = new GpodderSyncService(
            _gpodderStore, new GpodderClient(), _data, _playback, _episodes, _feedStore,
            saveAsync: _saver.RequestSaveAsync, uiDispatch: DispatchToUi);

        Log.Information("cfg at {Cfg}", _configStore.FilePath);
        Log.Information("lib at {Lib}", _libraryStore.FilePath);

        // apply audio player prefs
        _engineSvc.ApplyPrefsTo(_player);

        // mpris2 d-bus service (linux only)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _mpris = new MprisService(_data, _player, _playback, _episodes, _feedStore);
            _ = Task.Run(async () =>
            {
                try { await _mpris.StartAsync(); }
                catch (Exception ex) { Log.Warning(ex, "MPRIS D-Bus unavailable"); }
            });
        }

        // ui init
        Application.Init();
        _ui = new UiShell(_memLog);
        try { _data.LastSelectedFeedId = _ui.AllFeedId; }
        catch
        {
            // ignored
        }

        _ui.Build();
        UiComposer.UpdateWindowTitleWithDownloads(_ui, _data, _episodes);

        UiComposer.ScrollAllToTopOnIdle(_ui, _data, _episodes);

        // theme resolve (default = user)
        var themeChoice = UiThemeResolver.Resolve(cli.Theme, _data.ThemePref);
        try
        {
            _ui.SetTheme(themeChoice.Mode);
            if (themeChoice.ShouldPersistPref != null)
            {
                _data.ThemePref = themeChoice.ShouldPersistPref;
                await _saver.RequestSaveAsync();
            }
        }
        catch
        {
            // ignored
        }

        // Build the CmdCases container once every store, service and player
        // exists. Downstream UI wiring + NetworkMonitor hold references to
        // individual UseCases (e.g. ViewUseCase) so we construct it here
        // before NetworkMonitor starts.
        Func<string, Task> engineSwitch = pref => _engineSvc!.SwitchAsync(_player!, pref, _saver!.RequestSaveAsync);
        var cases = new StuiPodcast.App.Command.UseCases.CmdCases(
            ui: _ui, data: _data, persist: _saver.RequestSaveAsync,
            episodes: _episodes!, feedStore: _feedStore!, queue: _queue!,
            audioPlayer: _player!, playback: _playback!, dlm: _downloader!,
            switchEngine: engineSwitch, sync: _gpodder);

        // network monitor
        _net = new NetworkMonitor(_data, _ui, _saver.RequestSaveAsync, _episodes, cases.View);
        _net.Start(out _netTimerToken);

        // wire ui behaviors (sorter, lookups, events)
        _ui.EpisodeSorter = eps => UiComposer.ApplySort(eps, _data, _feedStore);
        _ui.SetUnplayedHint(_data.UnplayedOnly);
        _ui.SetPlayerPlacement(_data.PlayerAtTop);

        // reflect initial engine capabilities in the UI (e.g. disable speed buttons on MediaFoundation)
        _ui.UpdateSpeedEnabled((_player.Capabilities & PlayerCapabilities.Speed) != 0);

        // lookups
        _ui.SetQueueLookup(id => _queue!.Contains(id));
        _ui.SetDownloadStateLookup(id => _app!.IsDownloaded(id) ? DownloadState.Done : DownloadState.None);
        _ui.SetOfflineLookup(() => !_data.NetworkOnline);

        // theme change handler
        _ui.ThemeChanged += mode =>
        {
            _data.ThemePref = mode.ToString();
            _ = _saver!.RequestSaveAsync();
        };

        // downloader -> ui
        UiComposer.AttachDownloaderUi(_downloader, _ui, _data, _episodes);

        // Build the composition-root record now that every service exists.
        // UiComposer + CmdApplier pull dependencies from this record instead
        // of reaching into Program's private statics via reflection.
        var services = new AppServices(
            Ui: _ui, Data: _data, App: _app!,
            ConfigStore: _configStore!, LibraryStore: _libraryStore!,
            Episodes: _episodes!, FeedStore: _feedStore!, Queue: _queue!,
            Feeds: _feeds!, Player: _player!, Playback: _playback!,
            Downloader: _downloader!, DownloadLookup: _downloadLookup!,
            MemLog: _memLog, GpodderStore: _gpodderStore!, Gpodder: _gpodder,
            Saver: _saver!, Net: _net!, EngineSvc: _engineSvc!,
            Cases: cases
        );

        // build remaining ui behaviors
        UiComposer.WireUi(
            ctx: services,
            save: _saver.RequestSaveAsync,
            engineSwitch: engineSwitch,
            updateTitle: () => UiComposer.UpdateWindowTitleWithDownloads(_ui!, _data, services.Episodes),
            hasFeedWithUrl: HasFeedWithUrl
        );

        // progress persistence tick (ui timer)
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
                        });
                }
            }
            catch { }
            return true;
        });

        // apply cli flags (post-ui)
        CmdApplier.ApplyPostUiFlags(
            cli, _ui, _data, _player!, _playback!, _memLog, _saver.RequestSaveAsync, _downloader,
            engineSwitch,
            _episodes, _feedStore, _queue, cases, _gpodder);

        // initial lists
        UiComposer.ShowInitialLists(services);

        // gpodder auto-sync on startup
        if (_gpodder != null && _gpodder.ShouldAutoSync && _data.NetworkOnline)
            _ = Task.Run(async () =>
            {
                try { await _gpodder.SyncAsync(); }
                catch (Exception ex) { Log.Warning(ex, "gPodder auto-sync startup failed"); }
            });

        try { Application.Run(); }
        finally
        {
            Log.Information("shutdown begin");
            try { Application.MainLoop?.RemoveTimeout(_uiTimer); }
            catch
            {
                // ignored
            }

            try { if (_netTimerToken is not null) Application.MainLoop?.RemoveTimeout(_netTimerToken); }
            catch
            {
                // ignored
            }

            try { _player?.Stop(); }
            catch
            {
                // ignored
            }

            try { _player?.Dispose(); }
            catch
            {
                // ignored
            }

            try { _downloadLookup?.Dispose(); }
            catch
            {
                // ignored
            }

            try { _downloader?.Dispose(); }
            catch
            {
                // ignored
            }

            if (_mpris != null)
                try { await _mpris.DisposeAsync(); } catch { }

            // gpodder push-on-exit
            if (_gpodder != null && _gpodder.ShouldAutoSync && _data.NetworkOnline)
                try { await _gpodder.PushAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _gpodder?.Dispose();

            if (!SkipSaveOnExit) { await _saver!.RequestSaveAsync(flush:true); }

            try { _feeds?.Dispose(); }
            catch
            {
                // ignored
            }

            try { _playback?.Dispose(); }
            catch
            {
                // ignored
            }

            try { _app?.Dispose(); }
            catch
            {
                // ignored
            }

            try { Application.Shutdown(); }
            catch
            {
                // ignored
            }

            ResetHard();
            try { Log.CloseAndFlush(); }
            catch
            {
                // ignored
            }

            Log.Information("shutdown end");
        }
    }
    #endregion

    #region public api / compatibility
    // helpers exposed for other modules
    internal static bool MarkExiting() => Interlocked.Exchange(ref _exitOnce, 1) == 1;

    internal static object? NetTimerToken => _netTimerToken;
    internal static object? UiTimerToken  => _uiTimer;
    internal static DownloadManager? DownloaderInstance => _downloader;
    internal static MemoryLogSink MemLogSinkInstance => _memLog;

    public static bool IsDownloaded(Guid episodeId) => _app?.IsDownloaded(episodeId) ?? false;
    public static bool TryGetLocalPath(Guid episodeId, out string? path)
    {
        if (_app != null && _app.TryGetLocalPath(episodeId, out path)) return true;
        path = null;
        return false;
    }
    #endregion

    #region helpers
    // Run the given action on the Terminal.Gui main loop and await its completion.
    // Falls back to synchronous execution if no loop is running (tests, CLI-only flows).
    private static Task DispatchToUi(Action action)
    {
        var loop = Application.MainLoop;
        if (loop == null) { action(); return Task.CompletedTask; }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Invoke(() =>
        {
            try { action(); tcs.TrySetResult(true); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // resolve configuration directory
    private static string ResolveConfigDir()
    {
        var baseConfigDir =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        return Path.Combine(baseConfigDir, "podliner");
    }

    // check if feed with url already exists (O(1) via FeedStore URL index)
    private static bool HasFeedWithUrl(string url)
        => _feedStore?.ContainsUrl(url) ?? false;

    // print version to stdout
    private static void PrintVersion()
    {
        var asm  = typeof(Program).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver  = string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() ?? "0.0.0" : info;

        string rid;
        try { rid = RuntimeInformation.RuntimeIdentifier; }
        catch { rid = $"{Environment.OSVersion.Platform}-{RuntimeInformation.OSArchitecture}".ToLowerInvariant(); }

        Console.WriteLine($"podliner {ver} ({rid})");
    }

    // print simple help
    private static void PrintHelp()
    {
        // keep help short and readable
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
        Console.WriteLine("  --log-dir <DIR>          write logs to DIR");
        Console.WriteLine("  --no-file-logs           disable file logging (stdout only)");
    }
 

    // try to restore terminal state on exit
    private static void ResetHard()
    {
        try
        {
            Console.Write(
                "\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l" + // mouse off
                "\x1b[?2004l" +                                           // bracketed paste off
                "\x1b[?25h"   +                                           // cursor on
                "\x1b[0m"     +                                           // sgr reset
                "\x1b[?1049l"                                            // leave alt screen
            );
            Console.Out.Flush();
            Console.Write("\x1bc"); // RIS
            Console.Out.Flush();
        }
        catch { }
    }
    #endregion
}
