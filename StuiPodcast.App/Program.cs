using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using Terminal.Gui;

using StuiPodcast.Core;
using StuiPodcast.Infra;

class Program
{
    // SaveAsync-Throttle (klassenweite States)
    static readonly object _saveGate = new();
    static DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    static bool _savePending = false;
    static bool _saveRunning = false;

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

        // Auto-Advance: EINZIGE Quelle ist der Coordinator
        Playback.AutoAdvanceSuggested += next =>
        {
            // Auswahl konsistent zur Feed-Sortierung setzen
            var list = Data.Episodes
                .Where(e => e.FeedId == next.FeedId)
                .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                .ToList();

            var i = list.FindIndex(e => e.Id == next.Id);
            if (i >= 0) UI!.SelectEpisodeIndex(i);

            // Abspielen & UI aktualisieren
            Playback!.Play(next);
            UI!.SetWindowTitle(next.Title);
            UI!.ShowDetails(next);
            UI!.SetNowPlaying(next.Id);
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
            if (ep == null) return;

            // Verlauf sofort stempeln → hilft beim Restore
            ep.LastPlayedAt = DateTimeOffset.Now;
            _ = SaveAsync();

            Playback!.Play(ep);
            UI.SetWindowTitle(ep.Title);

            // NowPlaying für Pfeil/Status
            UI.SetNowPlaying(ep.Id);
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
            .FirstOrDefault();

        if (last == null)
        {
            // Fallback: aktuell selektierte (falls vorhanden)
            last = UI.GetSelectedEpisode();
        }

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

        // Player-State treibt UI + Persist
        Player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                UI.UpdatePlayerUI(s);
                Playback!.PersistProgressTick(
                    s,
                    eps => {
                        var fid = UI.GetSelectedFeedId();
                        if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
                    },
                    Data.Episodes);
            }
            catch
            {
                // UI robust halten
            }
        });

        // UI-Refresh Watchdog (redundant, aber pragmatisch)
        _uiTimer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            try
            {
                UI.UpdatePlayerUI(Player.State);
                Playback!.PersistProgressTick(
                    Player.State,
                    eps => {
                        var fid = UI.GetSelectedFeedId();
                        if (fid != null) UI.SetEpisodesForFeed(fid.Value, eps);
                    },
                    Data.Episodes);
            }
            catch { /* robust bleiben */ }

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

    // Legacy-Compat: aktuell No-Op (wurde früher für End-Transition genutzt)
    static void ResetAutoAdvance() { }
}
