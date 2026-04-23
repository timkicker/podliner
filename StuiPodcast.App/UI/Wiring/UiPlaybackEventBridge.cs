using StuiPodcast.App.Bootstrap;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using Terminal.Gui;

namespace StuiPodcast.App.UI.Wiring;

// Bridges PlaybackCoordinator + IAudioPlayer events into UiShell updates:
// snapshot ticks refresh the player bar and active-row progress, status
// transitions drive the loading overlay, and raw player-state changes feed
// the progress persistence tick in PlaybackCoordinator.
internal static class UiPlaybackEventBridge
{
    public static void Wire(AppServices ctx)
    {
        var ui = ctx.Ui;
        var data = ctx.Data;
        var player = ctx.Player;
        var playback = ctx.Playback;
        var episodeStore = ctx.Episodes;

        WireSnapshot(ui, data, player, playback, episodeStore);
        WireStatus(ui, playback);
        WireStateChanged(ui, player, playback);
    }

    static void WireSnapshot(UiShell ui, AppData data, IAudioPlayer player, PlaybackCoordinator playback, Services.IEpisodeStore episodeStore)
    {
        // Track the last episode whose title we pushed to the window label so
        // we don't re-resolve and re-set the same string 4×/sec. Network
        // transitions update the title via NetworkMonitor separately.
        Guid? lastTitleId = null;

        playback.SnapshotAvailable += snap => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui == null || player == null) return;

                ui.UpdatePlayerSnapshot(snap, player.State.Volume0_100);
                ui.UpdateSpeedEnabled((player.Capabilities & PlayerCapabilities.Speed) != 0);
                // Keep the playing episode row's progress bar fresh with a
                // single-row update; the periodic full refresh used to do
                // this but rebuilt the whole list.
                ui.RefreshActiveProgress(snap);

                // Chapters tab live-highlight: only if the snapshot's
                // episode is what the chapters tab is currently rendering.
                // The tab (via UiShell) ignores updates for other ids.
                if (snap.EpisodeId is Guid snapId)
                    ui.UpdateChapterHighlight(snapId, snap.Position.TotalSeconds);

                var nowId = ui.GetNowPlayingId();
                if (nowId is Guid nid && snap.EpisodeId == nid && nid != lastTitleId)
                {
                    var ep = episodeStore.Find(nid);
                    if (ep != null)
                    {
                        ui.SetWindowTitle((!data.NetworkOnline ? "[OFFLINE] " : "") + (ep.Title ?? "—"));
                        lastTitleId = nid;
                    }
                }
                else if (nowId == null && lastTitleId != null)
                {
                    lastTitleId = null; // reset so next play re-sets title
                }
            }
            catch { }
        });
    }

    static void WireStatus(UiShell ui, PlaybackCoordinator playback)
    {
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
    }

    static void WireStateChanged(UiShell ui, IAudioPlayer player, PlaybackCoordinator playback)
    {
        player.StateChanged += s => Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui == null || playback == null) return;

                playback.PersistProgressTick(
                    s,
                    eps => {
                        var fid = ui.GetSelectedFeedId();
                        if (fid != null) ui.SetEpisodesForFeed(fid.Value, eps);
                    });
                ui.UpdateSpeedEnabled((player.Capabilities & PlayerCapabilities.Speed) != 0);
            }
            catch { }
        });
    }
}
