using Serilog;
using Podliner.App.Bootstrap;
using Podliner.Core;

namespace Podliner.App.UI.Wiring;

// Subscribes UiShell events that route raw user input into the command
// dispatcher: the Command event (colon-commands from the minibuffer) and
// SearchApplied (live-typed search queries).
internal static class UiCommandWiring
{
    public static void Wire(
        AppServices ctx,
        Func<Task> save)
    {
        WireCommand(ctx, save);
        WireSearch(ctx);
    }

    static void WireCommand(AppServices ctx, Func<Task> save)
    {
        var ui = ctx.Ui;
        var data = ctx.Data;
        var audioPlayer = ctx.Player;
        var playback = ctx.Playback;
        var episodeStore = ctx.Episodes;
        var feedStore = ctx.FeedStore;
        var queueService = ctx.Queue;
        var syncService = ctx.Gpodder;
        var cases = ctx.Cases;

        ui.Command += cmd =>
        {
            Log.Debug("cmd {Cmd}", cmd);

            if (CmdRouter.HandleQueue(cases, cmd))
            {
                ui.SetQueueOrder(queueService.Snapshot());
                ui.RefreshEpisodesForSelectedFeed(episodeStore.Snapshot());
                return;
            }

            if (CmdRouter.HandleDownloads(cases, cmd))
                return;

            CmdRouter.Handle(cmd, audioPlayer, playback, ui, ctx.MemLog, data, save, ctx.Downloader,
                episodeStore, feedStore, queueService, cases, syncService);
        };
    }

    static void WireSearch(AppServices ctx) => WireSearch(ctx.Ui, ctx.Episodes);

    // Narrow overload: search only needs the shell and the episode store, so
    // it can be exercised without the rest of the composition root.
    public static void WireSearch(IUiShell ui, Services.IEpisodeStore episodeStore)
    {
        ui.SearchApplied += query =>
        {
            var fid = ui?.GetSelectedFeedId();
            IEnumerable<Episode> list = episodeStore.Snapshot();
            if (fid != null) list = list.Where(e => e.FeedId == fid.Value);

            if (!string.IsNullOrWhiteSpace(query))
                list = list.Where(e =>
                    (e.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

            if (fid != null) ui?.SetEpisodesForFeed(fid.Value, list);
        };
    }
}
