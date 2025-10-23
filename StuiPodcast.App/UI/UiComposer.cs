using System.Reflection;
using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

// ==========================================================
// UI composition & behaviors
// ==========================================================
static class UiComposer
{
    
    public static void ScrollAllToTopOnIdle(UiShell ui, AppData data)
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

    
    public static void UpdateWindowTitleWithDownloads(UiShell ui, AppData data)
    {
        var offlinePrefix = !data.NetworkOnline ? "[OFFLINE] " : "";
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
            var len = (double)e.DurationMs;
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
            if (len <= 60_000) return pos >= (long)(len * 0.98) || len - pos <= 500;
            return pos >= (long)(len * 0.995) || len - pos <= 2000;
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

    public static void AttachDownloaderUi(DownloadManager downloader, UiShell ui, AppData data)
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

            if (st.State == DownloadState.Running && DateTime.UtcNow - _dlLastUiPulse > TimeSpan.FromMilliseconds(500))
            {
                _dlLastUiPulse = DateTime.UtcNow;
                Application.MainLoop?.Invoke(() => ui?.RefreshEpisodesForSelectedFeed(data.Episodes));
            }
        };

        downloader.EnsureRunning();
    }

    public static void WireUi(
        UiShell ui,
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
            CmdRouter.ApplyList(ui, data);
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
                var msg = localPath == null ? "∅ Offline: not downloaded" : "No playable source";
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

            if (CmdRouter.HandleQueue(cmd, ui, data, save))
            {
                ui.SetQueueOrder(data.Queue);
                ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                return;
            }

            if (CmdRouter.HandleDownloads(cmd, ui, data, ProgramDownloader(), save))
                return;

            CmdRouter.Handle(cmd, player, playback, ui, ProgramLog(), data, save, ProgramDownloader(), engineSwitch);
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

    public static void ShowInitialLists(UiShell ui, AppData data)
    {
        ui.SetFeeds(data.Feeds, data.LastSelectedFeedId);
        ui.SetUnplayedHint(data.UnplayedOnly);
        CmdRouter.ApplyList(ui, data);

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

    static void QuitApp(UiShell ui, SwappablePlayer player, FeedService feeds, Func<Task> save)
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