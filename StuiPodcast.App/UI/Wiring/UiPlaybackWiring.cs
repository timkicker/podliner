using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using Terminal.Gui;

namespace StuiPodcast.App.UI.Wiring;

// Subscribes UiShell events that trigger playback actions from the episode
// pane: PlaySelected launches the selected episode (with queue-trim + local
// fallback logic) and TogglePlayedRequested flips the manual-played marker.
internal static class UiPlaybackWiring
{
    public static void Wire(AppServices ctx, Func<Task> save)
    {
        WirePlaySelected(ctx, save);
        WireTogglePlayed(ctx, save);
    }

    static void WirePlaySelected(AppServices ctx, Func<Task> save)
    {
        var ui = ctx.Ui;
        var data = ctx.Data;
        var app = ctx.App;
        var playback = ctx.Playback;
        var audioPlayer = ctx.Player;
        var episodeStore = ctx.Episodes;
        var queueService = ctx.Queue;

        ui.PlaySelected += () =>
        {
            var ep = ui?.GetSelectedEpisode();
            if (ep == null || audioPlayer == null || playback == null || ui == null) return;

            ep.Progress.LastPlayedAt = DateTimeOffset.UtcNow;
            _ = save();

            TrimQueueIfViewingQueue(ui, episodeStore, queueService, ep, save);

            string? localPath = app!.TryGetLocalPath(ep.Id, out var lp) ? lp : null;
            bool isRemote = IsRemote(localPath, ep);

            ShowLoading(ui, audioPlayer, isRemote);

            var source = ResolvePlaySource(data, localPath, ep);
            if (source == null)
            {
                ui.SetPlayerLoading(false);
                ui.ShowOsd(localPath == null ? "offline: not downloaded" : "no playable source", 1500);
                return;
            }

            StartPlayback(playback, ep, source, ui, data);
            ArmLocalFileFallbackIfNeeded(audioPlayer, playback, ep, localPath, ui);
        };
    }

    static void WireTogglePlayed(AppServices ctx, Func<Task> save)
    {
        var ui = ctx.Ui;
        var episodeStore = ctx.Episodes;

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
            if (fid != null) ui.SetEpisodesForFeed(fid.Value, episodeStore.Snapshot());
            ui.ShowDetails(ep);
        };
    }

    // ── play helpers ─────────────────────────────────────────────────────────

    // When playing from the virtual Queue feed, trim every queue entry up to
    // and including the played episode so the queue pane reflects progress.
    static void TrimQueueIfViewingQueue(UiShell ui, IEpisodeStore episodeStore, IQueueService queue, Episode ep, Func<Task> save)
    {
        var curFeed = ui.GetSelectedFeedId();
        if (curFeed is not Guid fid || fid != VirtualFeedsCatalog.Queue) return;

        if (!queue.TrimUpToInclusive(ep.Id)) return;

        ui.SetQueueOrder(queue.Snapshot());
        ui.RefreshEpisodesForSelectedFeed(episodeStore.Snapshot());
        _ = save();
    }

    static bool IsRemote(string? localPath, Episode ep)
        => string.IsNullOrWhiteSpace(localPath)
           && !string.IsNullOrWhiteSpace(ep.AudioUrl)
           && ep.AudioUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    static void ShowLoading(UiShell ui, IAudioPlayer audioPlayer, bool isRemote)
    {
        var baseline = TimeSpan.Zero;
        try { baseline = audioPlayer.State.Position; } catch { }
        ui.SetPlayerLoading(true, isRemote ? "loading…" : "opening…", baseline);
    }

    // Picks the audio source based on user preference + availability. Returns
    // null when nothing is playable (e.g. offline with no local copy).
    static string? ResolvePlaySource(AppData data, string? localPath, Episode ep)
    {
        var mode = (data.PlaySource ?? "auto").Trim().ToLowerInvariant();
        var online = data.NetworkOnline;

        return mode switch
        {
            "local"  => localPath,
            "remote" => ep.AudioUrl,
            _        => localPath ?? (online ? ep.AudioUrl : null)
        };
    }

    // Swap AudioUrl to the resolved source for the duration of Play() and
    // restore it afterwards so persisted episode metadata isn't tainted with
    // a file:// URI.
    static void StartPlayback(PlaybackCoordinator playback, Episode ep, string source, UiShell ui, AppData data)
    {
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
    }

    // Some engines fail silently when handed a raw file path. If 600 ms after
    // Play() the engine is still idle we retry with an explicit file:// URI.
    static void ArmLocalFileFallbackIfNeeded(IAudioPlayer audioPlayer, PlaybackCoordinator playback, Episode ep, string? localPath, UiShell ui)
    {
        if (localPath == null) return;

        Application.MainLoop?.AddTimeout(TimeSpan.FromMilliseconds(600), _ =>
        {
            try
            {
                var s = audioPlayer.State;
                if (s.IsPlaying || s.Position != TimeSpan.Zero) return false;

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
            catch { }
            return false;
        });
    }
}
