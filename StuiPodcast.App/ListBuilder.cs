using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App;

internal static class ListBuilder
{
    public static List<Episode> BuildCurrentList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        IEnumerable<Episode> baseList = data.Episodes;

        if (feedId == null) return new List<Episode>();
        if (data.UnplayedOnly) baseList = baseList.Where(e => !e.ManuallyMarkedPlayed);

        if (feedId == VirtualFeeds.Saved)           baseList = baseList.Where(e => e.Saved);
        else if (feedId == VirtualFeeds.Downloaded) baseList = baseList.Where(e => Program.IsDownloaded(e.Id));
        else if (feedId == VirtualFeeds.History)    baseList = baseList.Where(e => e.Progress.LastPlayedAt != null);
        else if (feedId != VirtualFeeds.All)        baseList = baseList.Where(e => e.FeedId == feedId);

        if (feedId == VirtualFeeds.History)
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
