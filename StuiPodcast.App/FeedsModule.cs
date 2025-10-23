using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App;

internal static class FeedsModule
{
    public static void ExecAddFeed(string[] args, Shell ui)
    {
        var url = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(url)) ui.RequestAddFeed(url);
        else ui.ShowOsd("usage: :add <rss-url>");
    }

    public static void ExecFeed(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        Guid? target = arg switch
        {
            "all"        => VirtualFeeds.All,
            "saved"      => VirtualFeeds.Saved,
            "downloaded" => VirtualFeeds.Downloaded,
            "history"    => VirtualFeeds.History,
            "queue"      => VirtualFeeds.Queue,
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

    public static void RemoveSelectedFeed(Shell ui, AppData data, Func<Task> persist)
    {
        var fid = ui.GetSelectedFeedId();
        if (fid is null) { ui.ShowOsd("No feed selected"); return; }

        if (fid == VirtualFeeds.All || fid == VirtualFeeds.Saved || fid == VirtualFeeds.Downloaded || fid == VirtualFeeds.History)
        { ui.ShowOsd("Can't remove virtual feeds"); return; }

        var feed = data.Feeds.FirstOrDefault(f => f.Id == fid);
        if (feed == null) { ui.ShowOsd("Feed not found"); return; }

        int removedEps = data.Episodes.RemoveAll(e => e.FeedId == fid);
        data.Feeds.RemoveAll(f => f.Id == fid);

        _ = persist();

        data.LastSelectedFeedId = VirtualFeeds.All;

        ui.SetFeeds(data.Feeds, data.LastSelectedFeedId);
        ViewModule.ApplyList(ui, data);

        ui.ShowOsd($"Removed feed: {feed.Title} ({removedEps} eps)");
    }
}
