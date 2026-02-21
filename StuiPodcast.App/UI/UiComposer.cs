using System.Reflection;
using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;
using Terminal.Gui;
using System.Collections.Generic;
using System.Linq;
using StuiPodcast.App.Bootstrap;


namespace StuiPodcast.App.UI;


static class UiComposer
{
    #region startup helpers
    // scroll feeds/episodes to top on first idle
    public static void ScrollAllToTopOnIdle(UiShell ui, AppData data)
    {
        Application.MainLoop?.AddIdle(() =>
        {
            try
            {
                data.LastSelectedFeedId = ui.AllFeedId;
                ui.EnsureSelectedFeedVisibleAndTop();
            } catch { }
            return false;
        });

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
    #endregion

    #region window title
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
    #endregion

    #region sorting
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
    #endregion

    #region downloader
    public static void AttachDownloaderUi(DownloadManager downloader, UiShell? ui, AppData data)
{
    // per-download state cache
    var byId = new Dictionary<Guid, DownloadState>();
    var progress = new Dictionary<Guid, (long bytes, long? total, DownloadState state)>();

    DateTime lastPulse = DateTime.MinValue;

    void updateBadge()
    {
        // badge: done/active/total and overall percent
        int done   = byId.Values.Count(s => s == DownloadState.Done);
        int active = byId.Values.Count(s => s == DownloadState.Queued
                                            || s == DownloadState.Running
                                            || s == DownloadState.Verifying);
        int total  = done + active;

        if (total <= 0)
        {
            ui?.SetDownloadBadge(null);
            return;
        }

        // bytes-weighted percent across items with known totals
        long sumBytes = 0;
        long sumTotal = 0;

        foreach (var (_, (bytes, totalBytes, st)) in progress.ToArray())
        {
            if (totalBytes is { } T && T > 0)
            {
                // done counts as 100%, running/verifying contribute current bytes
                if (st == DownloadState.Done)
                {
                    sumBytes += T;
                    sumTotal += T;
                }
                else if (st == DownloadState.Running || st == DownloadState.Verifying)
                {
                    var b = Math.Clamp(bytes, 0, T);
                    sumBytes += b;
                    sumTotal += T;
                }
            }
        }

        int pct;
        if (sumTotal > 0)
        {
            pct = (int)Math.Round(100.0 * sumBytes / sumTotal);
        }
        else
        {
            // fallback: no total bytes known → simple done/total percent
            pct = (int)Math.Round(100.0 * done / Math.Max(1, total));
        }

        ui?.SetDownloadBadge($"{done}/{total} • {pct}%");

        // optional: show osd when batch ends
        if (active == 0 && done > 0)
            ui?.ShowOsd($"downloads {done}/{total} • {pct}%");
    }


    downloader.StatusChanged += (id, st) =>
    {
        // update state cache
        byId[id] = st.State;
        
        // maintain progress entries
        progress[id] = (st.BytesReceived, st.TotalBytes, st.State);

        // prune progress on failed/canceled
        if (st.State == DownloadState.Failed || st.State == DownloadState.Canceled)
        {
            progress.Remove(id);
        }

        // ensure done reports 100% if bytes < total
        if (st.State == DownloadState.Done && progress.TryGetValue(id, out var p) && p.total is { } T)
        {
            progress[id] = (T, T, DownloadState.Done);
        }


        Application.MainLoop?.Invoke(() =>
        {
            ui?.RefreshEpisodesForSelectedFeed(data.Episodes);
            if (ui != null)
            {
                UpdateWindowTitleWithDownloads(ui, data);

                // small status osd
                switch (st.State)
                {
                    case DownloadState.Queued: ui.ShowOsd("dl queued", 300); break;
                    case DownloadState.Running: ui.ShowOsd("dl ⇣", 300); break;
                    case DownloadState.Verifying: ui.ShowOsd("dl ≈", 300); break;
                    case DownloadState.Done: ui.ShowOsd("dl ✓", 500); break;
                    case DownloadState.Failed: ui.ShowOsd("dl !", 900); break;
                    case DownloadState.Canceled: ui.ShowOsd("dl ×", 400); break;
                }
            }

            // update badge
            updateBadge();
        });

        // light ui pulse while running
        if (st.State == DownloadState.Running && DateTime.UtcNow - lastPulse > TimeSpan.FromMilliseconds(500))
        {
            lastPulse = DateTime.UtcNow;
            Application.MainLoop?.Invoke(() => ui?.RefreshEpisodesForSelectedFeed(data.Episodes));
        }
    };

    downloader.EnsureRunning();
}

    
    #endregion

    #region ui wiring
    public static void WireUi(
        UiShell ui,
        AppData data,
        AppFacade app,
        FeedService? feeds,
        PlaybackCoordinator? playback,
        SwappableAudioPlayer? audioPlayer,
        Func<Task> save,
        Func<string, Task> engineSwitch,
        Action updateTitle,
        Func<string, bool> hasFeedWithUrl,
        GpodderSyncService? syncService = null)
    {
        // quit
        ui.QuitRequested += () =>
        {
            if (feeds != null) QuitApp(ui, audioPlayer, feeds, save);
        };

        // add feed
        ui.AddFeedRequested += async url =>
        {
            if (feeds == null) return;
            if (string.IsNullOrWhiteSpace(url)) { ui.ShowOsd("add feed: url missing", 1500); return; }

            Log.Information("ui/addfeed url={Url}", url);
            ui.ShowOsd("adding…", 800);

            try
            {
                if (hasFeedWithUrl(url)) { ui.ShowOsd("already added", 1200); return; }
            }
            catch
            {
                // ignored
            }

            try
            {
                var f = await feeds.AddFeedAsync(url);
                app?.SaveNow();
                Log.Information("ui/addfeed ok id={Id} title={Title}", f.Id, f.Title);

                data.LastSelectedFeedId = f.Id;
                _ = save();

                ui.SetFeeds(data.Feeds, f.Id);
                ui.SetEpisodesForFeed(f.Id, data.Episodes);
                ui.SelectEpisodeIndex(0);

                ui.ShowOsd("feed added ✓", 1200);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ui/addfeed failed url={Url}", url);
                ui.ShowOsd($"add failed: {ex.Message}", 2200);
            }
        };
        
        ui.RemoveFeedRequested += async () =>
        {
            var fid = ui.GetSelectedFeedId();
            if (fid is null) { ui.ShowOsd("no feed selected", 1200); return; }

            try
            {
                await feeds?.RemoveFeedAsync(fid.Value)!;   // ← persist + update app data + save

                // reset ui
                var next = data.Feeds.FirstOrDefault()?.Id;
                ui.SetFeeds(data.Feeds, next);
                if (next != null) { ui.SetEpisodesForFeed(next.Value, data.Episodes); ui.SelectEpisodeIndex(0); }
                else              { ui.SetEpisodesForFeed(ui.AllFeedId, data.Episodes); }

                ui.ShowOsd("feed removed ✓", 1200);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ui/removefeed failed id={Id}", fid);
                ui.ShowOsd($"remove failed: {ex.Message}", 2200);
            }
        };



        // refresh
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

        // feed selection change
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

        // episode selection change
        ui.EpisodeSelectionChanged += () => { _ = save(); };

        // play selected
        ui.PlaySelected += () =>
        {
            var ep = ui?.GetSelectedEpisode();
            if (ep == null || audioPlayer == null || playback == null || ui == null) return;

            var curFeed = ui.GetSelectedFeedId();

            ep.Progress.LastPlayedAt = DateTimeOffset.UtcNow;
            _ = save();

            if (curFeed is Guid fid && fid == VirtualFeedsCatalog.Queue)
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
            try { baseline = audioPlayer?.State.Position ?? TimeSpan.Zero; } catch { }
            ui.SetPlayerLoading(true, isRemote ? "loading…" : "opening…", baseline);

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
                var msg = localPath == null ? "offline: not downloaded" : "no playable source";
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
                        var s = audioPlayer.State;
                        if (!s.IsPlaying && s.Position == TimeSpan.Zero)
                        {
                            try { audioPlayer.Stop(); } catch { }
                            var fileUri = new Uri(localPath).AbsoluteUri;

                            var old = ep.AudioUrl;
                            try
                            {
                                ep.AudioUrl = fileUri;
                                playback.Play(ep);
                                ui.ShowOsd("retry (file://)");
                            }
                            finally { ep.AudioUrl = old; }
                        }
                    }
                    catch { }
                    return false;
                });
            }
        };

        // theme toggle
        ui.ToggleThemeRequested += () => ui.ToggleTheme();

        // played toggle
        ui.TogglePlayedRequested += () =>
        {
            var ep = ui.GetSelectedEpisode();
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

        // command router
        ui.Command += cmd =>
        {
            Log.Debug("cmd {Cmd}", cmd);
            if (audioPlayer == null || playback == null || Program.SkipSaveOnExit) { }

            if (CmdRouter.HandleQueue(cmd, ui, data, save))
            {
                ui.SetQueueOrder(data.Queue);
                ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                return;
            }

            if (CmdRouter.HandleDownloads(cmd, ui, data, ProgramDownloader(), save))
                return;

            CmdRouter.Handle(cmd, audioPlayer, playback, ui, ProgramLog(), data, save, ProgramDownloader(), engineSwitch, syncService);
        };

        // search
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

        // local accessors
        static DownloadManager ProgramDownloader() => (DownloadManager)typeof(Program)
            .GetField("_downloader", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        static MemoryLogSink ProgramLog() => (MemoryLogSink)typeof(Program)
            .GetField("_memLog", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    }
    #endregion

    #region initialization
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

        var player = (SwappableAudioPlayer)typeof(Program)
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
                    ui.UpdateSpeedEnabled((player.Capabilities & PlayerCapabilities.Speed) != 0);

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
                        ui.SetPlayerLoading(true, "loading…", null);
                        break;
                    case PlaybackStatus.SlowNetwork:
                        ui.SetPlayerLoading(true, "connecting… (slow)", null);
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
                    ui.UpdateSpeedEnabled((player.Capabilities & PlayerCapabilities.Speed) != 0);
                }
            }
            catch { }
        });
    }
    #endregion

    #region shutdown
    static void QuitApp(UiShell ui, SwappableAudioPlayer audioPlayer, FeedService feeds, Func<Task> save)
    {
        if (Program.MarkExiting()) return;

        try { var t  = Program.NetTimerToken; if (t  is not null) Application.MainLoop?.RemoveTimeout(t); } catch { }
        try { var ut = Program.UiTimerToken;  if (ut is not null) Application.MainLoop?.RemoveTimeout(ut); } catch { }

        try { Program.DownloaderInstance?.Stop(); } catch { }
        try { audioPlayer?.Stop(); } catch { }

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
    #endregion
}
