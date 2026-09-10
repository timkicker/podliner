using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Services;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI.Wiring;

// Subscribes UiShell selection events. When the user navigates between feeds
// or episodes the UI re-renders the episode list and persists the selection
// so the next launch restores position.
internal static class UiSelectionWiring
{
    public static void Wire(AppServices ctx, Func<Task> save)
        => Wire(ctx.Ui, ctx.Data, ctx.Episodes, save);

    // Takes only what it uses rather than the whole composition root, so the
    // subscription can be exercised with FakeUiShell and FakeEpisodeStore.
    public static void Wire(IUiShell ui, AppData data, IEpisodeStore episodeStore, Func<Task> save)
    {
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
