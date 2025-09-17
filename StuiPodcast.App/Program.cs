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

        // events
        UI.QuitRequested += () => QuitApp();

        UI.AddFeedRequested += async url =>
        {
            var f = await Feeds!.AddFeedAsync(url);
            UI.SetFeeds(Data.Feeds, f.Id);
            UI.SelectFeed(f.Id);
            UI.SetEpisodesForFeed(f.Id, Data.Episodes);
        };

        UI.RefreshRequested += async () =>
        {
            await Feeds!.RefreshAllAsync();
            var keep = UI.GetSelectedFeedId();
            UI.SetFeeds(Data.Feeds, keep);
            if (keep is Guid fid) UI.SetEpisodesForFeed(fid, Data.Episodes);
        };

        UI.SelectedFeedChanged += () =>
        {
            if (UI.GetSelectedFeedId() is Guid fid)
                UI.SetEpisodesForFeed(fid, Data.Episodes);
        };

        UI.PlaySelected += () =>
        {
            var ep = UI.GetSelectedEpisode();
            if (ep == null) return;
            Playback!.Play(ep);
            UI.SetWindowTitle(ep.Title);
        };

        UI.ToggleThemeRequested += () => UI.ToggleTheme();

        UI.TogglePlayedRequested += () =>
        {
            var ep = UI.GetSelectedEpisode();
            if (ep == null) return;
            ep.Played = !ep.Played;
            if (ep.Played && ep.LengthMs is long len) ep.LastPosMs = len;
            _ = SaveAsync();
            if (UI.GetSelectedFeedId() is Guid fid) UI.SetEpisodesForFeed(fid, Data.Episodes);
            UI.ShowDetails(ep);
        };

        // sehr kleine Command-Map ohne externen Router
        UI.Command += HandleUiCommand;

        UI.SearchApplied += query =>
        {
            if (UI.GetSelectedFeedId() is not Guid fid) return;

            var list = Data.Episodes.Where(e => e.FeedId == fid);
            if (!string.IsNullOrWhiteSpace(query))
                list = list.Where(e =>
                    (e.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

            UI.SetEpisodesForFeed(fid, list);
        };

        // initial
        UI.SetFeeds(Data.Feeds);
        if (UI.GetSelectedFeedId() is Guid initId)
            UI.SetEpisodesForFeed(initId, Data.Episodes);

        // player → UI
        Player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            UI.UpdatePlayerUI(s);
            Playback!.PersistProgressTick(s, eps =>
            {
                if (UI.GetSelectedFeedId() is Guid fid) UI.SetEpisodesForFeed(fid, eps);
            }, Data.Episodes);
        });

        _uiTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            UI.UpdatePlayerUI(Player.State);
            Playback!.PersistProgressTick(Player.State, eps =>
            {
                if (UI.GetSelectedFeedId() is Guid fid) UI.SetEpisodesForFeed(fid, eps);
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

    // ------ Commands aus Shell (Buttons/Keys) ------
    static void HandleUiCommand(string cmd)
    {
        if (UI is null) return;

        // quit-commands
        if (cmd is ":q" or ":quit")
        {
            QuitApp();
            return;
        }

        if (Player is null) return;

        try
        {
            if (cmd == ":toggle")
            {
                Player.TogglePause();
                UI.UpdatePlayerUI(Player.State);
                return;
            }

            if (cmd.StartsWith(":seek"))
            {
                var arg = cmd.Substring(5).Trim();
                if (string.IsNullOrWhiteSpace(arg)) return;

                if (arg.EndsWith("%") && double.TryParse(arg.TrimEnd('%'), out var pct))
                {
                    if (Player.State.Length is TimeSpan len)
                    {
                        var pos = TimeSpan.FromMilliseconds(len.TotalMilliseconds * Math.Clamp(pct / 100.0, 0, 1));
                        Player.SeekTo(pos);
                    }
                    return;
                }

                if (arg.StartsWith("+") || arg.StartsWith("-"))
                {
                    if (int.TryParse(arg, out var secsRel))
                        Player.SeekRelative(TimeSpan.FromSeconds(secsRel));
                    return;
                }

                var parts = arg.Split(':');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var mm)
                    && int.TryParse(parts[1], out var ss))
                {
                    Player.SeekTo(TimeSpan.FromSeconds(mm * 60 + ss));
                }
                return;
            }

            if (cmd.StartsWith(":vol"))
            {
                var arg = cmd.Substring(4).Trim();
                if (int.TryParse(arg, out var v))
                {
                    if (arg.StartsWith("+") || arg.StartsWith("-"))
                        Player.SetVolume(Player.State.Volume0_100 + v);
                    else
                        Player.SetVolume(v);
                }
                return;
            }

            if (cmd.StartsWith(":speed"))
            {
                var arg = cmd.Substring(6).Trim();
                if (double.TryParse(arg, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var sp))
                {
                    if (arg.StartsWith("+") || arg.StartsWith("-"))
                        Player.SetSpeed(Player.State.Speed + sp);
                    else
                        Player.SetSpeed(sp);
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "UI command failed: {Cmd}", cmd);
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
