using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal interface IUiShell
{
    void ShowOsd(string text, int ms = 1200);
    Episode? GetSelectedEpisode();
    Guid? GetSelectedFeedId();
    Guid? GetNowPlayingId();
    void SelectFeed(Guid id);
    void SetEpisodesForFeed(Guid feedId, IEnumerable<Episode> episodes);
    void RefreshEpisodesForSelectedFeed(IEnumerable<Episode> episodes);
    void SetQueueOrder(IReadOnlyList<Guid> ids);
    void SelectEpisodeIndex(int index);
    void SetWindowTitle(string? s);
    void ShowDetails(Episode e);
    void SetNowPlaying(Guid? episodeId);
    void RefreshActiveProgress(PlaybackSnapshot snap);
    void RequestAddFeed(string url);
    void RequestRemoveFeed();
    void RequestRefresh();
    void RequestQuit();
    void SetUnplayedFilterVisual(bool on);
    void ToggleTheme();
    void SetTheme(ThemeMode mode);
    void TogglePlayerPlacement();
    void SetPlayerPlacement(bool atTop);
    void SetFeeds(List<Feed> feeds, Guid? selectId = null);
    void ShowKeysHelp();
    void ShowLogsOverlay(int tail = 500);
    void SetHistoryLimit(int n);
    Func<IEnumerable<Episode>, IEnumerable<Episode>>? EpisodeSorter { get; set; }
    Guid AllFeedId { get; }
}
