using StuiPodcast.App.Bootstrap;

namespace StuiPodcast.App.UI.Wiring;

// Subscribes UiShell selection events. When the user navigates between feeds
// or episodes the UI re-renders the episode list and persists the selection
// so the next launch restores position.
internal static class UiSelectionWiring
{
    public static void Wire(AppServices ctx, Func<Task> save)
    {
        var ui = ctx.Ui;
        var data = ctx.Data;
        var episodeStore = ctx.Episodes;

        ui.SelectedFeedChanged += () =>
        {
            var fid = ui.GetSelectedFeedId();
            data.LastSelectedFeedId = fid;

            if (fid != null)
            {
                ui.SetEpisodesForFeed(fid.Value, episodeStore.Snapshot());
                ui.SelectEpisodeIndex(0);
            }

            _ = save();
        };

        ui.EpisodeSelectionChanged += () => { _ = save(); };
    }
}
