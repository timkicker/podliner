using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal interface IUiShell
{
    // Chapters tab — fed by external loader (wired in Program.cs to
    // ChaptersUseCase.LoadForUiAsync). UI pushes a Loading state, fires the
    // event, listener resolves + pushes result back via SetChaptersResult.
    event Action<Episode>? ChaptersLoadRequested;
    void SetChaptersLoading(string message);
    void SetChaptersResult(Guid episodeId, IReadOnlyList<Chapter> chapters, int activeIndex = -1);
    void SetChaptersEmpty(Guid episodeId, string message);
    // Called from the playback snapshot tick so the highlight tracks time.
    // Silent no-op if the chapters tab isn't visible or nothing loaded yet.
    void UpdateChapterHighlight(Guid episodeId, double posSeconds);

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
    void SetFeeds(IReadOnlyList<Feed> feeds, Guid? selectId = null);
    void ShowKeysHelp();
    void ShowLogsOverlay(int tail = 500);
    void SetHistoryLimit(int n);
    Func<IEnumerable<Episode>, IEnumerable<Episode>>? EpisodeSorter { get; set; }
    Guid AllFeedId { get; }
}
