using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Command.Module;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI.Wiring;

// Populates the UI on startup: feed sidebar, episode list for the initial
// feed, and — if available — the last-played episode (so the app opens on
// the track the user was listening to). Pure rendering, no event wiring.
internal static class UiInitialRender
{
    public static void Render(AppServices ctx)
    {
        var ui = ctx.Ui;
        var data = ctx.Data;
        var episodeStore = ctx.Episodes;
        var feedStore = ctx.FeedStore;

        CmdViewModule.ApplyFeedList(ui, data, feedStore, episodeStore);
        ui.SetUnplayedHint(data.UnplayedOnly);
        CmdRouter.ApplyList(ui, data, episodeStore);

        var initialFeed = ui.GetSelectedFeedId();
        if (initialFeed != null)
        {
            ui.SetEpisodesForFeed(initialFeed.Value, episodeStore.Snapshot());
            ui.SelectEpisodeIndex(0);
        }

        var last = PickLastPlayedEpisode(episodeStore, ui);
        if (last != null) ShowLastPlayed(ui, data, episodeStore, last);
    }

    static Episode? PickLastPlayedEpisode(Services.IEpisodeStore episodeStore, UiShell ui)
        => episodeStore.Snapshot()
            .OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Progress.LastPosMs)
            .FirstOrDefault()
           ?? ui.GetSelectedEpisode();

    static void ShowLastPlayed(UiShell ui, AppData data, Services.IEpisodeStore episodeStore, Episode last)
    {
        ui.SelectFeed(last.FeedId);
        ui.SetEpisodesForFeed(last.FeedId, episodeStore.Snapshot());

        var list = episodeStore.WhereByFeed(last.FeedId)
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();

        var idx = Math.Max(0, list.FindIndex(e => e.Id == last.Id));
        ui.SelectEpisodeIndex(idx);

        ui.ShowStartupEpisode(last, data.Volume0_100, data.Speed);
    }
}
