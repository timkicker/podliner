using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.UseCases;

// Entry points for :add, :feed, :rmfeed. Delegates the actual feed add /
// remove work back to UiShell events (which Program.cs hooks into
// FeedService via UiFeedWiring) — this keeps refresh + persistence logic
// in one place and avoids a duplicate pipeline.
internal sealed class FeedUseCase
{
    readonly IUiShell _ui;
    readonly AppData _data;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;

    public FeedUseCase(IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes)
    {
        _ui = ui;
        _data = data;
        _persist = persist;
        _episodes = episodes;
    }

    public void ExecAddFeed(string[] args)
    {
        var url = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(url)) _ui.RequestAddFeed(url);
        else _ui.ShowOsd("usage: :add <rss-url>");
    }

    public void ExecFeed(string[] args)
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
            _data.LastSelectedFeedId = fid;
            _ = _persist();
            _ui.SelectFeed(fid);
            _ui.SetEpisodesForFeed(fid, _episodes.Snapshot());
        }
        else _ui.ShowOsd("usage: :feed all|saved|downloaded|history|queue");
    }

    public void RemoveSelectedFeed()
    {
        var fid = _ui.GetSelectedFeedId();
        if (fid is null) { _ui.ShowOsd("No feed selected"); return; }

        if (fid == VirtualFeedsCatalog.All || fid == VirtualFeedsCatalog.Saved
                                           || fid == VirtualFeedsCatalog.Downloaded || fid == VirtualFeedsCatalog.History)
        { _ui.ShowOsd("Can't remove virtual feeds"); return; }

        _ui.RequestRemoveFeed();
    }
}
