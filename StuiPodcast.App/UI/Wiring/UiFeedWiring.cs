using Serilog;
using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Services;

namespace StuiPodcast.App.UI.Wiring;

// Subscribes UiShell events that drive feed-lifecycle actions: quit, add,
// remove, refresh, and the theme toggle. All handlers run on the Terminal.Gui
// thread so they can mutate UI state directly after persisted stores settle.
internal static class UiFeedWiring
{
    public static void Wire(
        AppServices ctx,
        Func<Task> save,
        Func<string, bool> hasFeedWithUrl)
    {
        WireQuit(ctx, save);
        WireAddFeed(ctx, save, hasFeedWithUrl);
        WireRemoveFeed(ctx);
        WireRefresh(ctx, save);
        WireThemeToggle(ctx);
    }

    static void WireQuit(AppServices ctx, Func<Task> save)
    {
        var ui = ctx.Ui;
        var feeds = ctx.Feeds;
        var audioPlayer = ctx.Player;

        ui.QuitRequested += () =>
        {
            if (feeds != null) UiComposer.QuitApp(ui, audioPlayer, feeds, save);
        };
    }

    static void WireAddFeed(AppServices ctx, Func<Task> save, Func<string, bool> hasFeedWithUrl)
    {
        var ui = ctx.Ui;
        var data = ctx.Data;
        var app = ctx.App;
        var feeds = ctx.Feeds;
        var feedStore = ctx.FeedStore;
        var episodeStore = ctx.Episodes;

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
            catch { /* ignored */ }

            try
            {
                var f = await feeds.AddFeedAsync(url);
                app?.SaveNow();
                Log.Information("ui/addfeed ok id={Id} title={Title}", f.Id, f.Title);

                data.LastSelectedFeedId = f.Id;
                _ = save();

                ui.SetFeeds(feedStore.Snapshot(), f.Id);
                ui.SetEpisodesForFeed(f.Id, episodeStore.Snapshot());
                ui.SelectEpisodeIndex(0);

                ui.ShowOsd("feed added ✓", 1200);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ui/addfeed failed url={Url}", url);
                ui.ShowOsd($"add failed: {ex.Message}", 2200);
            }
        };
    }

    static void WireRemoveFeed(AppServices ctx)
    {
        var ui = ctx.Ui;
        var feeds = ctx.Feeds;
        var feedStore = ctx.FeedStore;
        var episodeStore = ctx.Episodes;

        ui.RemoveFeedRequested += async () =>
        {
            var fid = ui.GetSelectedFeedId();
            if (fid is null) { ui.ShowOsd("no feed selected", 1200); return; }

            try
            {
                await feeds?.RemoveFeedAsync(fid.Value)!;

                var snapshot = feedStore.Snapshot();
                var next = snapshot.FirstOrDefault()?.Id;
                ui.SetFeeds(snapshot, next);
                if (next != null) { ui.SetEpisodesForFeed(next.Value, episodeStore.Snapshot()); ui.SelectEpisodeIndex(0); }
                else              { ui.SetEpisodesForFeed(ui.AllFeedId, episodeStore.Snapshot()); }

                ui.ShowOsd("feed removed ✓", 1200);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ui/removefeed failed id={Id}", fid);
                ui.ShowOsd($"remove failed: {ex.Message}", 2200);
            }
        };
    }

    static void WireRefresh(AppServices ctx, Func<Task> save)
    {
        var ui = ctx.Ui;
        var data = ctx.Data;
        var feeds = ctx.Feeds;
        var feedStore = ctx.FeedStore;
        var episodeStore = ctx.Episodes;

        var cases = ctx.Cases;
        ui.RefreshRequested += async () =>
        {
            await feeds!.RefreshAllAsync();

            var selected = ui.GetSelectedFeedId() ?? data.LastSelectedFeedId;
            ui.SetFeeds(feedStore.Snapshot(), selected);

            if (selected != null)
                ui.SetEpisodesForFeed(selected.Value, episodeStore.Snapshot());

            cases.View.ApplyList();
        };
    }

    static void WireThemeToggle(AppServices ctx)
    {
        var ui = ctx.Ui;
        ui.ToggleThemeRequested += () => ui.ToggleTheme();
    }
}
