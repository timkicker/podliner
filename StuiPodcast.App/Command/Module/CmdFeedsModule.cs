using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class CmdFeedsModule
{
    public static void ExecAddFeed(string[] args, UiShell ui)
    {
        var url = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(url)) ui.RequestAddFeed(url);
        else ui.ShowOsd("usage: :add <rss-url>");
    }

    public static void ExecFeed(string[] args, UiShell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        Guid? target = arg switch
        {
            "all"        => VirtualFeedsCatalog.All,
            "saved"      => VirtualFeedsCatalog.Saved,
            "downloaded" => VirtualFeedsCatalog.Downloaded,
            "history"    => VirtualFeedsCatalog.History,
            "queue"      => VirtualFeedsCatalog.Queue,
            _            => null
        };

        if (target is Guid fid)
        {
            data.LastSelectedFeedId = fid;
            _ = persist();
            ui.SelectFeed(fid);
            ui.SetEpisodesForFeed(fid, data.Episodes);
        }
        else ui.ShowOsd("usage: :feed all|saved|downloaded|history|queue");
    }

    public static void RemoveSelectedFeed(UiShell ui, AppData data, Func<Task> persist)
    {
        var fid = ui.GetSelectedFeedId();
        if (fid is null) { ui.ShowOsd("No feed selected"); return; }

        if (fid == VirtualFeedsCatalog.All || fid == VirtualFeedsCatalog.Saved || fid == VirtualFeedsCatalog.Downloaded || fid == VirtualFeedsCatalog.History)
        { ui.ShowOsd("Can't remove virtual feeds"); return; }

        var feed = data.Feeds.FirstOrDefault(f => f.Id == fid);
        if (feed == null) { ui.ShowOsd("Feed not found"); return; }

        int removedEps = data.Episodes.RemoveAll(e => e.FeedId == fid);
        data.Feeds.RemoveAll(f => f.Id == fid);

        _ = persist();

        data.LastSelectedFeedId = VirtualFeedsCatalog.All;

        ui.SetFeeds(data.Feeds, data.LastSelectedFeedId);
        CmdViewModule.ApplyList(ui, data);

        ui.ShowOsd($"Removed feed: {feed.Title} ({removedEps} eps)");
    }
}
