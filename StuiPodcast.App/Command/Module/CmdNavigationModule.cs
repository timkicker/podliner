using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class CmdNavigationModule
{
    public static void ExecGoto(string[] args, UiShell ui, AppData data)
    {
        var arg = (args.Length > 0 ? args[0] : "").ToLowerInvariant();
        if (arg is "top" or "start") { SelectAbsolute(0, ui, data); return; }
        if (arg is "bottom" or "end") { SelectAbsolute(int.MaxValue, ui, data); return; }
    }

    public static void SelectRelative(int dir, UiShell ui, AppData data, bool playAfterSelect = false, PlaybackCoordinator? playback = null)
    {
        var list = EpisodeListBuilder.BuildCurrentList(ui, data);
        if (list.Count == 0) return;

        var cur = ui.GetSelectedEpisode();
        int idx = 0;
        if (cur != null)
        {
            var i = list.FindIndex(x => x.Id == cur.Id);
            idx = i >= 0 ? i : 0;
        }

        int target = dir > 0 ? Math.Min(idx + 1, list.Count - 1) : Math.Max(idx - 1, 0);
        ui.SelectEpisodeIndex(target);

        if (playAfterSelect && playback != null)
        {
            var ep = list[target];
            playback.Play(ep);
            ui.SetWindowTitle(ep.Title);
            ui.ShowDetails(ep);
            ui.SetNowPlaying(ep.Id);
        }
    }

    public static void SelectAbsolute(int index, UiShell ui, AppData data)
    {
        var list = EpisodeListBuilder.BuildCurrentList(ui, data);
        if (list.Count == 0) return;
        int target = Math.Clamp(index, 0, list.Count - 1);
        ui.SelectEpisodeIndex(target);
    }

    public static void SelectMiddle(UiShell ui, AppData data)
    {
        var list = EpisodeListBuilder.BuildCurrentList(ui, data);
        if (list.Count == 0) return;
        int target = list.Count / 2;
        ui.SelectEpisodeIndex(target);
    }

    public static void JumpUnplayed(int dir, UiShell ui, PlaybackCoordinator playback, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        IEnumerable<Episode> baseList = data.Episodes;

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

        var cur = ui.GetSelectedEpisode();
        var startIdx = cur is null ? -1 : eps.FindIndex(x => ReferenceEquals(x, cur) || x.Id == cur.Id);
        int i = startIdx;

        for (int step = 0; step < eps.Count; step++)
        {
            i = dir > 0 ? (i + 1 + eps.Count) % eps.Count : (i - 1 + eps.Count) % eps.Count;
            if (!eps[i].ManuallyMarkedPlayed)
            {
                var target = eps[i];
                playback.Play(target);
                ui.SetWindowTitle(target.Title);
                ui.ShowDetails(target);
                ui.SetNowPlaying(target.Id);
                return;
            }
        }
    }
}
