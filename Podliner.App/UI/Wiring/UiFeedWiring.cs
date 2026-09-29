using Serilog;
using Podliner.App.Bootstrap;
using Podliner.Core;
using Podliner.Infra;
using Podliner.App.Services;

namespace Podliner.App.UI.Wiring;

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
        => WireRemoveFeed(ctx.Ui, ctx.Feeds, ctx.FeedStore, ctx.Episodes);

    // Narrow overload: removal only needs the shell, the feed service and the
    // two stores, so it can run against fakes.
    public static void WireRemoveFeed(IUiShell ui, IFeedService feeds,
                                      Services.IFeedStore feedStore, Services.IEpisodeStore episodeStore)
    {
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
        => WireRefresh(ctx.Ui, ctx.Data, ctx.Refresher, ctx.FeedStore, ctx.Episodes, ctx.Cases.View);

    // Narrow overload. The failure aggregation below is the part worth
    // testing: one bad feed shows its reason, several show only a count.
    public static void WireRefresh(IUiShell ui, AppData data, FeedRefresher refresher,
                                   Services.IFeedStore feedStore, Services.IEpisodeStore episodeStore,
                                   Command.UseCases.ViewUseCase view)
    {
        refresher.Started += () => ui.SetFeedsTitle(refresher.SidebarTitle(UIGlyphSet.TitleSeparator));

        // After every pass, the timed ones included (#32): new episodes have
        // to reach the screen whether or not anyone asked for them.
        refresher.Finished += () =>
        {
            var selected = ui.GetSelectedFeedId() ?? data.LastSelectedFeedId;
            ui.SetFeeds(feedStore.Snapshot(), selected);

            if (selected != null)
                ui.SetEpisodesForFeed(selected.Value, episodeStore.Snapshot());

            view.ApplyList();
            ui.SetFeedsTitle(refresher.SidebarTitle(UIGlyphSet.TitleSeparator));
        };

        // :refresh. Only this path talks back; a timed pass stays silent,
        // since an hourly message about a server that is down would nag.
        ui.RefreshRequested += async () =>
        {
            if (!data.NetworkOnline)
            {
                ui.ShowOsd("refresh: offline — nothing fetched", 2000);
                return;
            }

            var outcome = await refresher.RunAsync();

            if (outcome.Result == RefreshResult.Skipped)
            {
                ui.ShowOsd("refresh: already running", 1200);
                return;
            }

            var failures = outcome.Failures;
            if (failures.Count == 0)
                ui.ShowOsd("refreshed ✓", 1000);
            else if (failures.Count == 1)
                ui.ShowOsd($"refresh: {failures[0]}", 2800);
            else
                ui.ShowOsd($"refresh: {failures.Count} feeds failed — :logs for details", 2800);
        };
    }

    // Called by the refresh timer. The title is updated every time because
    // the age in it moves on even when nothing is fetched.
    public static Task TickRefresh(IUiShell ui, FeedRefresher refresher)
    {
        ui.SetFeedsTitle(refresher.SidebarTitle(UIGlyphSet.TitleSeparator));
        return refresher.TickAsync();
    }

    static void WireThemeToggle(AppServices ctx)
    {
        var ui = ctx.Ui;
        ui.ToggleThemeRequested += () => ui.ShowOsd($"theme: {ui.ToggleTheme()}");
    }
}
