using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using Terminal.Gui;

namespace StuiPodcast.App.UI.Wiring;

// Translates DownloadManager events into UiShell updates: the download badge
// (done/active/percent), per-state OSD hints, and throttled list refreshes
// while downloads are running.
internal static class UiDownloaderBridge
{
    public static void Attach(DownloadManager downloader, UiShell? ui, AppData data, IEpisodeStore episodes)
    {
        // per-download state cache
        var byId = new Dictionary<Guid, DownloadState>();
        var progress = new Dictionary<Guid, (long bytes, long? total, DownloadState state)>();

        DateTime lastPulse = DateTime.MinValue;

        void UpdateBadge()
        {
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

            int pct = sumTotal > 0
                ? (int)Math.Round(100.0 * sumBytes / sumTotal)
                : (int)Math.Round(100.0 * done / Math.Max(1, total));

            ui?.SetDownloadBadge($"{done}/{total} • {pct}%");

            if (active == 0 && done > 0)
                ui?.ShowOsd($"downloads {done}/{total} • {pct}%");
        }

        downloader.StatusChanged += (id, st) =>
        {
            // During an active download, DownloadManager fires StatusChanged ~every
            // 400ms with state=Running for progress pulses. We want UI side-effects
            // (OSD, full list refresh) only on real state transitions; progress
            // byte updates still flow into the `progress` dictionary for the badge.
            var prevState = byId.TryGetValue(id, out var ps) ? (DownloadState?)ps : null;
            bool isTransition = prevState != st.State;

            byId[id] = st.State;
            progress[id] = (st.BytesReceived, st.TotalBytes, st.State);

            // prune progress on failed/canceled
            if (st.State == DownloadState.Failed || st.State == DownloadState.Canceled)
                progress.Remove(id);

            // ensure done reports 100% if bytes < total
            if (st.State == DownloadState.Done && progress.TryGetValue(id, out var p) && p.total is { } T)
                progress[id] = (T, T, DownloadState.Done);

            if (isTransition)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    ui?.RefreshEpisodesForSelectedFeed(episodes.Snapshot());
                    if (ui != null)
                    {
                        UiComposer.UpdateWindowTitleWithDownloads(ui, data, episodes);
                        switch (st.State)
                        {
                            case DownloadState.Queued:    ui.ShowOsd("dl queued", 300); break;
                            case DownloadState.Running:   ui.ShowOsd("dl ⇣", 300);    break;
                            case DownloadState.Verifying: ui.ShowOsd("dl ≈", 300);    break;
                            case DownloadState.Done:      ui.ShowOsd("dl ✓", 500);    break;
                            case DownloadState.Failed:    ui.ShowOsd("dl !", 900);    break;
                            case DownloadState.Canceled:  ui.ShowOsd("dl ×", 400);    break;
                        }
                    }
                    UpdateBadge();
                });
            }

            // Light ui pulse while running: refresh at most every 2s to reflect
            // changing byte counts on the episode row. Was every 500ms — too spammy
            // for multi-GB downloads with thousands of episodes.
            if (st.State == DownloadState.Running && DateTime.UtcNow - lastPulse > TimeSpan.FromSeconds(2))
            {
                lastPulse = DateTime.UtcNow;
                Application.MainLoop?.Invoke(() =>
                {
                    ui?.RefreshEpisodesForSelectedFeed(episodes.Snapshot());
                    UpdateBadge();
                });
            }
        };

        downloader.EnsureRunning();
    }
}
