using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal interface IUiShell
{
    // Chapters tab — fed by external loader (wired in Program.cs to
    // ChaptersUseCase.LoadForUiAsync). UI pushes a Loading state, fires the
    // event, listener resolves + pushes result back via SetChaptersResult.
    event Action<Episode>? ChaptersLoadRequested;

    // Selection events. On the interface so the wiring that reacts to them
    // can be built against IUiShell and tested with FakeUiShell.
    event Action? SelectedFeedChanged;
    event Action? EpisodeSelectionChanged;
    // Live-typed search from the `/` minibuffer.
    event Action<string>? SearchApplied;

    // Feed-lifecycle requests raised by keys, the menu bar or commands.
    event Action? QuitRequested;
    event Action? RemoveFeedRequested;
    event Action? ToggleThemeRequested;
    event Func<string, Task>? AddFeedRequested;
    event Func<Task>? RefreshRequested;

    // Raw colon-command typed into the minibuffer, routed to CmdRouter.
    event Action<string>? Command;
    // Enter on the episode list.
    event Action? PlaySelected;
    // The `m` key.
    event Action? TogglePlayedRequested;

    // Caption on the episode list that reflects the unplayed-only filter.
    void SetUnplayedHint(bool on);
    // Restores the player bar to the episode the app opens on.
    void ShowStartupEpisode(Episode ep, int? volume = null, double? speed = null);
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
    // Download counter in the window title. Null clears it.
    void SetDownloadBadge(string? text);
    void SetQueueOrder(IReadOnlyList<Guid> ids);
    void SelectEpisodeIndex(int index);
    void SetWindowTitle(string? s);
    void ShowDetails(Episode e);
    void SetNowPlaying(Guid? episodeId);
    void RefreshActiveProgress(PlaybackSnapshot snap);

    // Player-bar updates driven by the playback event bridge.
    void UpdatePlayerSnapshot(PlaybackSnapshot snap, int volume0to100);
    void UpdateSpeedEnabled(bool enabled);
    void SetPlayerLoading(bool on, string? text = null, TimeSpan? baseline = null);
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
    // Re-syncs the layout with the terminal size and repaints. Recovery path
    // for a missed Terminal.Gui resize poll (issue #4).
    void ForceRedraw();
    void SetHistoryLimit(int n);
    Func<IEnumerable<Episode>, IEnumerable<Episode>>? EpisodeSorter { get; set; }
    Guid AllFeedId { get; }
}
