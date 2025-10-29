using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Services;

internal static class EpisodeListBuilder
{
    public static List<Episode> BuildCurrentList(UiShell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        IEnumerable<Episode> baseList = data.Episodes;

        if (feedId == null) return new List<Episode>();
        if (data.UnplayedOnly) baseList = baseList.Where(e => !e.ManuallyMarkedPlayed);

        if (feedId == VirtualFeedsCatalog.Saved)           baseList = baseList.Where(e => e.Saved);
        else if (feedId == VirtualFeedsCatalog.Downloaded) baseList = baseList.Where(e => Program.IsDownloaded(e.Id));
        else if (feedId == VirtualFeedsCatalog.History)    baseList = baseList.Where(e => e.Progress.LastPlayedAt != null);
        else if (feedId != VirtualFeedsCatalog.All)        baseList = baseList.Where(e => e.FeedId == feedId);

        if (feedId == VirtualFeedsCatalog.History)
        {
            return baseList
                .OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.Progress.LastPosMs)
                .ToList();
        }

        return baseList
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();
    }
}
