using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.UseCases;

// Episode-pane keyboard navigation: relative/absolute selection, middle,
// jump-to-next-unplayed. Builds against the currently visible list
// (EpisodeListBuilder respects the selected virtual feed + unplayed filter)
// so navigation matches what the user actually sees.
internal sealed class NavigationUseCase
{
    readonly IUiShell _ui;
    readonly AppData _data;
    readonly IEpisodeStore _episodes;
    readonly PlaybackCoordinator _playback;

    public NavigationUseCase(IUiShell ui, AppData data, IEpisodeStore episodes, PlaybackCoordinator playback)
    {
        _ui = ui;
        _data = data;
        _episodes = episodes;
        _playback = playback;
    }

    public void ExecGoto(string[] args)
    {
        var arg = (args.Length > 0 ? args[0] : "").ToLowerInvariant();
        if (arg is "top" or "start") { SelectAbsolute(0); return; }
        if (arg is "bottom" or "end") { SelectAbsolute(int.MaxValue); return; }
    }

    public void SelectRelative(int dir, bool playAfterSelect = false)
    {
        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data, _episodes);
        if (list.Count == 0) return;

        var cur = _ui.GetSelectedEpisode();
        int idx = 0;
        if (cur != null)
        {
            var i = list.FindIndex(x => x.Id == cur.Id);
            idx = i >= 0 ? i : 0;
        }

        int target = dir > 0 ? Math.Min(idx + 1, list.Count - 1) : Math.Max(idx - 1, 0);
        _ui.SelectEpisodeIndex(target);

        if (playAfterSelect)
        {
            var ep = list[target];
            _playback.Play(ep);
            _ui.SetWindowTitle(ep.Title);
            _ui.ShowDetails(ep);
            _ui.SetNowPlaying(ep.Id);
        }
    }

    public void SelectAbsolute(int index)
    {
        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data, _episodes);
        if (list.Count == 0) return;
        int target = Math.Clamp(index, 0, list.Count - 1);
        _ui.SelectEpisodeIndex(target);
    }

    public void SelectMiddle()
    {
        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data, _episodes);
        if (list.Count == 0) return;
        int target = list.Count / 2;
        _ui.SelectEpisodeIndex(target);
    }

    public void JumpUnplayed(int dir)
    {
        var feedId = _ui.GetSelectedFeedId();
        if (feedId is null) return;

        IEnumerable<Episode> baseList = _episodes.Snapshot();

        if (feedId == VirtualFeedsCatalog.Saved) baseList = baseList.Where(e => e.Saved);
        else if (feedId == VirtualFeedsCatalog.Downloaded) baseList = baseList.Where(e => Program.IsDownloaded(e.Id));
        else if (feedId == VirtualFeedsCatalog.History) baseList = baseList.Where(e => e.Progress.LastPlayedAt != null);
        else if (feedId != VirtualFeedsCatalog.All) baseList = baseList.Where(e => e.FeedId == feedId);

        List<Episode> eps =
            feedId == VirtualFeedsCatalog.History
            ? baseList.OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
                      .ThenByDescending(e => e.Progress.LastPosMs).ToList()
            : baseList.OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue).ToList();

        if (eps.Count == 0) return;

        var cur = _ui.GetSelectedEpisode();
        var startIdx = cur is null ? -1 : eps.FindIndex(x => ReferenceEquals(x, cur) || x.Id == cur.Id);
        int i = startIdx;

        for (int step = 0; step < eps.Count; step++)
        {
            i = dir > 0 ? (i + 1 + eps.Count) % eps.Count : (i - 1 + eps.Count) % eps.Count;
            if (!eps[i].ManuallyMarkedPlayed)
            {
                var target = eps[i];
                _playback.Play(target);
                _ui.SetWindowTitle(target.Title);
                _ui.ShowDetails(target);
                _ui.SetNowPlaying(target.Id);
                return;
            }
        }
    }
}
