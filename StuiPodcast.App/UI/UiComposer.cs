using Serilog;
using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI.Wiring;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Download;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

// Top-level composer. Orchestrates startup rendering + event wiring by
// delegating to focused wiring classes in UI/Wiring. Also hosts the
// cross-cutting helpers (sort key evaluation, window-title formatting,
// idle scroll-to-top, shutdown sequence) that multiple wiring classes
// and Program.cs share.
static class UiComposer
{
    #region startup helpers

    // Scroll feeds/episodes to top on first idle so the initial view starts
    // at the newest item regardless of where the previous selection was.
    public static void ScrollAllToTopOnIdle(UiShell ui, AppData data, IEpisodeStore episodes)
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
                ui.SetEpisodesForFeed(ui.AllFeedId, episodes.Snapshot());
                Application.MainLoop!.AddIdle(() =>
                {
                    try { ui.ScrollEpisodesToTopAndFocus(); } catch { }
                    return false;
                });
            } catch { }
            return false;
        });
    }

    // Renders the window title using the now-playing episode (if any) and
    // prefixes "[OFFLINE]" when the network is flagged as offline. Shared by
    // NetworkMonitor, UiDownloaderBridge, and Program startup.
    public static void UpdateWindowTitleWithDownloads(UiShell ui, AppData data, IEpisodeStore episodes)
    {
        var offlinePrefix = !data.NetworkOnline ? "[OFFLINE] " : "";
        string baseTitle = "Podliner";
        try
        {
            var nowId = ui.GetNowPlayingId();
            if (nowId != null)
            {
                var ep = episodes.Find(nowId.Value);
                if (ep != null && !string.IsNullOrWhiteSpace(ep.Title))
                    baseTitle = ep.Title!;
            }
        }
        catch { }
        ui.SetWindowTitle($"{offlinePrefix}{baseTitle}");
    }

    #endregion

    #region sorting helpers

    // Shared "is this episode played" predicate used by both feed and episode
    // sort keys. Falls back to position-based heuristics when the user hasn't
    // explicitly marked the episode as played.
    private static bool IsPlayed(Episode e)
    {
        if (e.ManuallyMarkedPlayed) return true;
        var len = e.DurationMs;
        if (len <= 0) return false;
        var pos = e.Progress?.LastPosMs ?? 0;
        if (len <= 60_000) return pos >= (long)(len * 0.98) || len - pos <= 500;
        return pos >= (long)(len * 0.995) || len - pos <= 2000;
    }

    public static IEnumerable<Feed> ApplyFeedSort(IEnumerable<Feed> feeds, AppData data, IEpisodeStore episodeStore)
    {
        if (feeds == null) return Enumerable.Empty<Feed>();
        var by  = (data.FeedSortBy  ?? "title").Trim().ToLowerInvariant();
        var dir = (data.FeedSortDir ?? "asc").Trim().ToLowerInvariant();
        bool desc = dir == "desc";

        var episodes = (IEnumerable<Episode>)episodeStore.Snapshot();

        switch (by)
        {
            case "updated":
                var latest = episodes
                    .GroupBy(e => e.FeedId)
                    .ToDictionary(g => g.Key, g => g.Max(e => e.PubDate ?? DateTimeOffset.MinValue));
                return desc
                    ? feeds.OrderByDescending(f => latest.GetValueOrDefault(f.Id, DateTimeOffset.MinValue))
                    : feeds.OrderBy(f => latest.GetValueOrDefault(f.Id, DateTimeOffset.MinValue));

            case "unplayed":
                var counts = episodes
                    .GroupBy(e => e.FeedId)
                    .ToDictionary(g => g.Key, g => g.Count(e => !IsPlayed(e)));
                return desc
                    ? feeds.OrderByDescending(f => counts.GetValueOrDefault(f.Id, 0))
                    : feeds.OrderBy(f => counts.GetValueOrDefault(f.Id, 0));

            case "title":
            default:
                return desc
                    ? feeds.OrderByDescending(f => f.Title ?? "", StringComparer.OrdinalIgnoreCase)
                    : feeds.OrderBy(f => f.Title ?? "", StringComparer.OrdinalIgnoreCase);
        }
    }

    public static IEnumerable<Episode> ApplySort(IEnumerable<Episode> eps, AppData data, IFeedStore feedStore)
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

        string FeedTitle(Episode e) => feedStore.Find(e.FeedId)?.Title ?? "";

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
    }

    #endregion

    #region downloader

    // Delegates to UiDownloaderBridge. Kept here as a stable entry point so
    // Program.cs doesn't need to reach into the Wiring namespace directly.
    public static void AttachDownloaderUi(DownloadManager downloader, UiShell? ui, AppData data, IEpisodeStore episodes)
        => UiDownloaderBridge.Attach(downloader, ui, data, episodes);

    #endregion

    #region event wiring (delegation to Wiring/*)

    public static void WireUi(
        AppServices ctx,
        Func<Task> save,
        Func<AudioEngine, Task> engineSwitch,
        Action updateTitle,
        Func<string, bool> hasFeedWithUrl)
    {
        UiFeedWiring.Wire(ctx, save, hasFeedWithUrl);
        UiSelectionWiring.Wire(ctx, save);
        UiPlaybackWiring.Wire(ctx, save);
        UiCommandWiring.Wire(ctx, save, engineSwitch);
    }

    public static void ShowInitialLists(AppServices ctx)
    {
        UiInitialRender.Render(ctx);
        UiPlaybackEventBridge.Wire(ctx);
    }

    #endregion

    #region shutdown

    public static void QuitApp(UiShell ui, SwappableAudioPlayer audioPlayer, FeedService feeds, Func<Task> save)
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
