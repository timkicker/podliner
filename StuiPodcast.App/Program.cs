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

        // events → commands (centralized in CommandRouter)
        UI.QuitRequested += () => QuitApp();

        UI.Command += cmd =>
        {
            if (Feeds == null || Player == null || Playback == null || UI == null) return;
            _ = CommandRouter.HandleAsync(cmd, Data, Feeds, Player, Playback, UI, SaveAsync, MemLog);
        };

        UI.SearchApplied += q =>
        {
            if (Feeds == null || Player == null || Playback == null || UI == null) return;
            _ = CommandRouter.HandleAsync($":search {q}", Data, Feeds, Player, Playback, UI, SaveAsync, MemLog);
        };

        UI.ToggleThemeRequested += () => UI.ToggleTheme();

        UI.TogglePlayedRequested += () =>
        {
            if (Feeds == null || Player == null || Playback == null || UI == null) return;
            _ = CommandRouter.HandleAsync(":mark", Data, Feeds, Player, Playback, UI, SaveAsync, MemLog);
        };

        UI.SelectedFeedChanged += () =>
        {
            var fid = UI.GetSelectedFeedId();
            if (fid != null) UI.SetEpisodesForFeed(fid.Value, Data.Episodes);
        };

        UI.PlaySelected += () =>
        {
            var ep = UI.GetSelectedEpisode();
            if (ep == null || Playback == null || UI == null) return;
            Playback.Play(ep);
            UI.SetWindowTitle(ep.Title);
        };

        // initial lists
        UI.SetFeeds(Data.Feeds);
        var startFeedId = UI.GetSelectedFeedId();
        if (startFeedId != null) UI.SetEpisodesForFeed(startFeedId.Value, Data.Episodes);

        // player → UI updates + persistence tick
        Player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            UI.UpdatePlayerUI(s);
            Playback!.PersistProgressTick(s, (eps) =>
            {
                var fid = UI.GetSelectedFeedId();
                if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
            }, Data.Episodes);
        });

        _uiTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            UI.UpdatePlayerUI(Player.State);
            Playback!.PersistProgressTick(Player.State, (eps) =>
            {
                var fid = UI.GetSelectedFeedId();
                if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
            }, Data.Episodes);
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
