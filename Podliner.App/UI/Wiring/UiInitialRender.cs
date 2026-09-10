using Podliner.App.Bootstrap;
using Podliner.Core;

namespace Podliner.App.UI.Wiring;

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
        var cases = ctx.Cases;

        cases.View.ApplyFeedList();
        ui.SetUnplayedHint(data.UnplayedOnly);

        // The Queue view renders in queue order, and that order was only ever
        // pushed after a queue command. Without this the queue looks empty
        // after every restart even though it was persisted.
        ui.SetQueueOrder(ctx.Queue.Snapshot());
        cases.View.ApplyList();

        var initialFeed = ui.GetSelectedFeedId();
        if (initialFeed != null)
        {
            ui.SetEpisodesForFeed(initialFeed.Value, episodeStore.Snapshot());
            ui.SelectEpisodeIndex(0);
        }

        var last = PickLastPlayedEpisode(episodeStore, ui);
        if (last != null) ShowLastPlayed(ui, data, episodeStore, last);
    }

    // Decides which episode the app opens on: most recently played, then
    // furthest-progressed, falling back to whatever row is already selected
    // when nothing has ever been played.
    internal static Episode? PickLastPlayedEpisode(Services.IEpisodeStore episodeStore, IUiShell ui)
        => episodeStore.Snapshot()
            .OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Progress.LastPosMs)
            .FirstOrDefault()
           ?? ui.GetSelectedEpisode();

    static void ShowLastPlayed(IUiShell ui, AppData data, Services.IEpisodeStore episodeStore, Episode last)
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
