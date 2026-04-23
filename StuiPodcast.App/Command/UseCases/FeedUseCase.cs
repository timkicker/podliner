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
    readonly IFeedStore? _feedStore;

    public FeedUseCase(IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes, IFeedStore? feedStore = null)
    {
        _ui = ui;
        _data = data;
        _persist = persist;
        _episodes = episodes;
        _feedStore = feedStore;
    }

    public void ExecAddFeed(string[] args)
    {
        var url = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(url)) _ui.RequestAddFeed(url);
        else _ui.ShowOsd("usage: :add <rss-url>");
    }

    public void ExecFeed(string[] args)
    {
        args ??= Array.Empty<string>();

        if (args.Length >= 1)
        {
            var sub = args[0].Trim().ToLowerInvariant();
            if (sub == "speed")         { FeedSpeed(args); return; }
            if (sub == "auto-download") { FeedAutoDownload(args); return; }
        }

        var arg = string.Join(' ', args).Trim().ToLowerInvariant();
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
        else _ui.ShowOsd("usage: :feed all|saved|downloaded|history|queue | :feed speed <n> | :feed auto-download on|off");
    }

    // :feed speed <n> | off | show   — operates on currently selected feed.
    // A feed-scoped rate survives engine swaps via PlaybackCoordinator.Play.
    void FeedSpeed(string[] args)
    {
        var feed = ResolveSelectedRealFeed(out var why);
        if (feed == null) { _ui.ShowOsd(why); return; }

        if (args.Length < 2 || args[1].Trim().ToLowerInvariant() == "show")
        {
            _ui.ShowOsd(feed.SpeedOverride is { } s
                ? $"feed speed: {s:0.##}× (override)"
                : "feed speed: inheriting app default", 1500);
            return;
        }

        var arg = args[1].Trim().ToLowerInvariant();
        if (arg is "off" or "clear" or "none")
        {
            feed.SpeedOverride = null;
            _feedStore!.AddOrUpdate(feed);
            _ = _persist();
            _ui.ShowOsd($"feed speed: cleared for {feed.Title}", 1500);
            return;
        }

        if (!double.TryParse(arg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var val) || val <= 0)
        {
            _ui.ShowOsd($"usage: :feed speed <0.25-3.0|off>", 1800);
            return;
        }

        var clamped = Math.Clamp(val, 0.25, 3.0);
        feed.SpeedOverride = clamped;
        _feedStore!.AddOrUpdate(feed);
        _ = _persist();
        _ui.ShowOsd($"feed speed: {clamped:0.##}× for {feed.Title}", 1500);
    }

    // :feed auto-download on|off|toggle
    void FeedAutoDownload(string[] args)
    {
        var feed = ResolveSelectedRealFeed(out var why);
        if (feed == null) { _ui.ShowOsd(why); return; }

        var arg = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "toggle";
        bool next = arg switch
        {
            "on"  or "true"  or "1" or "yes" => true,
            "off" or "false" or "0" or "no"  => false,
            _                                => !feed.AutoDownload
        };

        feed.AutoDownload = next;
        _feedStore!.AddOrUpdate(feed);
        _ = _persist();
        _ui.ShowOsd($"auto-download: {(next ? "on" : "off")} for {feed.Title}", 1500);
    }

    Feed? ResolveSelectedRealFeed(out string why)
    {
        why = "";
        if (_feedStore == null) { why = "feed store unavailable"; return null; }
        var fid = _ui.GetSelectedFeedId();
        if (fid is null) { why = "no feed selected"; return null; }
        if (VirtualFeedsCatalog.IsVirtual(fid.Value)) { why = "can't configure virtual feeds"; return null; }
        var f = _feedStore.Find(fid.Value);
        if (f == null) { why = "feed not found"; return null; }
        return f;
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
