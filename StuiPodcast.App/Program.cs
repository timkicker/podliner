using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using StuiPodcast.App.Debug;
using Terminal.Gui;

using StuiPodcast.Core;
using StuiPodcast.Infra;

class Program
{
    static AppData Data = new();
    static FeedService? Feeds;
    static IPlayer? Player;

    static Shell? UI;
    static PlaybackCoordinator? Playback;
    static CommandRouter? Router;
    static MemoryLogSink MemLog = new(2000);

    static object? _uiTimer;
    static int _exitOnce = 0;

    static async Task Main()
    {
        ConfigureLogging();
        InstallGlobalErrorHandlers();

        Data  = await AppStorage.LoadAsync();
        Feeds = new FeedService(Data);
        if (Data.Feeds.Count == 0)
        {
            try { await Feeds.AddFeedAsync("https://themadestages.podigee.io/feed/mp3"); }
            catch (Exception ex) { Log.Warning(ex, "Could not add default feed"); }
        }

        Player   = new LibVlcPlayer();
        Playback = new PlaybackCoordinator(Data, Player, SaveAsync, MemLog);

        Console.TreatControlCAsInput = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; QuitApp(); };
        AppDomain.CurrentDomain.ProcessExit += (_, __) => TerminalUtil.ResetHard();

        Application.Init();

        UI = new Shell(MemLog);
        UI.Build();

        // Router
        Router = new CommandRouter(
            data: Data,
            feeds: Feeds!,
            player: Player!,
            playback: Playback!,
            ui: UI!,
            save: SaveAsync,
            quit: QuitApp
        );

        // wire shell → router (alles zentralisiert)
        UI.QuitRequested        += () => _ = Router.Handle(":q");
        UI.PlaySelected         += () => _ = Router.Handle(":play");
        UI.ToggleThemeRequested += () => _ = Router.Handle(":theme");
        UI.TogglePlayedRequested+= () => _ = Router.Handle(":mark");
        UI.Command              += cmd => _ = Router!.Handle(cmd);
        UI.SearchApplied        += q => _ = Router!.Handle($":search {q}");

        // initial lists
        UI.SetFeeds(Data.Feeds);
        var initFeed = UI.GetSelectedFeedId() ?? Data.Feeds.FirstOrDefault()?.Id;
        if (initFeed is Guid f0) UI.SetEpisodesForFeed(f0, Data.Episodes);

        // player → UI (+ persist-tick)
        Player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            UI.UpdatePlayerUI(s);
            // update right list to reflect progress/played flag
            var fid = UI.GetSelectedFeedId();
            if (fid is Guid g) UI.SetEpisodesForFeed(g, Data.Episodes);
            Playback!.PersistProgressTick(s, _ => { /* ignored, we update via UI above */ }, Data.Episodes);
        });

        _uiTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            UI.UpdatePlayerUI(Player.State);
            var fid = UI.GetSelectedFeedId();
            if (fid is Guid g) UI.SetEpisodesForFeed(g, Data.Episodes);
            Playback!.PersistProgressTick(Player.State, _ => { }, Data.Episodes);
            return true;
        });

        try { Application.Run(); }
        finally
        {
            try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }
            try { Player?.Stop(); } catch { }
            (Player as IDisposable)?.Dispose();
            await SaveAsync();
            try { Application.Shutdown(); } catch { }
            TerminalUtil.ResetHard();
            try { Log.CloseAndFlush(); } catch { }
        }
    }

    static async Task SaveAsync()
    {
        try { await AppStorage.SaveAsync(Data); }
        catch (Exception ex) { Log.Debug(ex, "save failed"); }
    }

    static void QuitApp()
    {
        if (System.Threading.Interlocked.Exchange(ref _exitOnce, 1) == 1) return;

        try { Application.MainLoop?.RemoveTimeout(_uiTimer); } catch { }
        try { (Player as IDisposable)?.Dispose(); } catch { }

        try { Application.RequestStop(); } catch { }
        try { Application.Shutdown(); } catch { }

        TerminalUtil.ResetHard();
        try { Log.CloseAndFlush(); } catch { }
        Environment.Exit(0);
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
}
