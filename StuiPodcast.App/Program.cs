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

        Player   = new LibVlcPlayer();
        Playback = new PlaybackCoordinator(Data, Player, SaveAsync, MemLog);

        // >> Restore Player prefs
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

        // >> Restore Player-Bar position
        UI.SetPlayerPlacement(Data.PlayerAtTop);

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

        // Commands via Router (mit Persist)
        UI.Command += cmd => CommandRouter.Handle(cmd, Player!, Playback!, UI!, MemLog, Data, SaveAsync);

        UI.SearchApplied += query =>
        {
            var fid = UI.GetSelectedFeedId();
            var list = Data.Episodes.AsEnumerable();
            if (fid != null) list = list.Where(e => e.FeedId == fid.Value);

            if (!string.IsNullOrWhiteSpace(query))
                list = list.Where(e =>
                    (e.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

            if (fid != null) UI.SetEpisodesForFeed(fid.Value, list);
        };

        // --- initial lists ---
        UI.SetFeeds(Data.Feeds, Data.LastSelectedFeedId);

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

        // player → UI + persist progress
        Player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            UI.UpdatePlayerUI(s);
            Playback!.PersistProgressTick(
                s,
                eps => {
                    var fid = UI.GetSelectedFeedId();
                    if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
                },
                Data.Episodes);
        });

        _uiTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            UI.UpdatePlayerUI(Player.State);
            Playback!.PersistProgressTick(
                Player.State,
                eps => {
                    var fid = UI.GetSelectedFeedId();
                    if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
                },
                Data.Episodes);
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
}
