using Podliner.App.Services;
using Podliner.App.UI;
using Podliner.Core;

namespace Podliner.App.Tests.Fakes;

sealed class FakeUiShell : IUiShell
{
    // --- recorded calls ---
    public List<(string Text, int Ms)> OsdMessages { get; } = new();
    public Episode? SelectedEpisode { get; set; }
    public Guid? SelectedFeedId { get; set; }
    public Guid? NowPlayingId { get; set; }
    public int LastSelectedIndex { get; set; } = -1;
    public List<Guid> QueueOrder { get; } = new();
    public string? LastWindowTitle { get; set; }
    public Episode? LastShownDetails { get; set; }
    public List<Feed> LastSetFeeds { get; } = new();
    public Guid? LastSetFeedsSelectId { get; set; }
    public bool? LastUnplayedFilterVisual { get; set; }
    public string? LastRequestedAddFeedUrl { get; set; }
    public bool RequestedRemoveFeed { get; set; }
    public bool RequestedRefresh { get; set; }
    public bool RequestedQuit { get; set; }
    public bool ThemeToggled { get; set; }
    public ThemeMode? LastSetTheme { get; set; }
    public bool PlayerPlacementToggled { get; set; }
    public bool? LastPlayerPlacement { get; set; }
    public bool KeysHelpShown { get; set; }
    public int? LastLogsOverlayTail { get; set; }
    public int? LastHistoryLimit { get; set; }
    public List<(Guid FeedId, List<Episode> Episodes)> SetEpisodeCalls { get; } = new();
    public Func<IEnumerable<Episode>, IEnumerable<Episode>>? EpisodeSorter { get; set; }
    public Guid AllFeedId => VirtualFeedsCatalog.All;

    // Chapters-tab recording.
    public string? LastChaptersLoading { get; private set; }
    public (Guid EpisodeId, List<Chapter> Chapters, int ActiveIdx)? LastChaptersResult { get; private set; }
    public (Guid EpisodeId, string Message)? LastChaptersEmpty { get; private set; }
    public (Guid EpisodeId, double PosSec)? LastChaptersHighlight { get; private set; }
    public event Action<Episode>? ChaptersLoadRequested;
    public void SetChaptersLoading(string message) => LastChaptersLoading = message;
    public void SetChaptersResult(Guid episodeId, IReadOnlyList<Chapter> chapters, int activeIndex = -1)
        => LastChaptersResult = (episodeId, chapters.ToList(), activeIndex);
    public void SetChaptersEmpty(Guid episodeId, string message)
        => LastChaptersEmpty = (episodeId, message);
    public void UpdateChapterHighlight(Guid episodeId, double posSeconds)
        => LastChaptersHighlight = (episodeId, posSeconds);
    public void FireChaptersLoadRequested(Episode ep) => ChaptersLoadRequested?.Invoke(ep);

    // --- IUiShell implementation ---

    public void ShowOsd(string text, int ms = 1200) => OsdMessages.Add((text, ms));
    public Episode? GetSelectedEpisode() => SelectedEpisode;
    public Guid? GetSelectedFeedId() => SelectedFeedId;
    public Guid? GetNowPlayingId() => NowPlayingId;

    public void SelectFeed(Guid id) => SelectedFeedId = id;

    public void SetEpisodesForFeed(Guid feedId, IEnumerable<Episode> episodes)
    {
        var list = episodes.ToList();
        SetEpisodeCalls.Add((feedId, list));
    }

    public void RefreshEpisodesForSelectedFeed(IEnumerable<Episode> episodes)
    {
        if (SelectedFeedId is Guid fid)
            SetEpisodesForFeed(fid, episodes);
    }

    public void SetQueueOrder(IReadOnlyList<Guid> ids)
    {
        QueueOrder.Clear();
        QueueOrder.AddRange(ids);
    }

    public void SelectEpisodeIndex(int index) => LastSelectedIndex = index;
    public int WindowTitleCalls { get; private set; }
    public void SetWindowTitle(string? s) { LastWindowTitle = s; WindowTitleCalls++; }
    public void ShowDetails(Episode e) => LastShownDetails = e;
    public void SetNowPlaying(Guid? episodeId) => NowPlayingId = episodeId;
    public PlaybackSnapshot? LastActiveProgressSnap { get; private set; }
    public void RefreshActiveProgress(PlaybackSnapshot snap) => LastActiveProgressSnap = snap;
    public void RequestAddFeed(string url) => LastRequestedAddFeedUrl = url;
    public void RequestRemoveFeed() => RequestedRemoveFeed = true;
    public void RequestRefresh() => RequestedRefresh = true;
    public void RequestQuit() => RequestedQuit = true;
    public void SetUnplayedFilterVisual(bool on) => LastUnplayedFilterVisual = on;
    public string? SearchFilter { get; private set; }
    public void SetSearchFilter(string? query) => SearchFilter = string.IsNullOrWhiteSpace(query) ? null : query;

    public ThemeMode NextToggleTheme { get; set; } = ThemeMode.MenuAccent;
    public ThemeMode ToggleTheme() { ThemeToggled = true; return NextToggleTheme; }
    public void SetTheme(ThemeMode mode) => LastSetTheme = mode;
    public void TogglePlayerPlacement() => PlayerPlacementToggled = true;
    public void SetPlayerPlacement(bool atTop) => LastPlayerPlacement = atTop;

    public void SetFeeds(IReadOnlyList<Feed> feeds, Guid? selectId = null)
    {
        LastSetFeeds.Clear();
        LastSetFeeds.AddRange(feeds);
        LastSetFeedsSelectId = selectId;
    }

    public void ShowKeysHelp() => KeysHelpShown = true;
    public void ShowLogsOverlay(int tail = 500) => LastLogsOverlayTail = tail;

    public readonly List<(PlaybackSnapshot Snap, int Volume)> PlayerSnapshots = new();
    public void UpdatePlayerSnapshot(PlaybackSnapshot snap, int volume0to100)
        => PlayerSnapshots.Add((snap, volume0to100));

    public bool? SpeedEnabled { get; private set; }
    public void UpdateSpeedEnabled(bool enabled) => SpeedEnabled = enabled;

    public readonly List<(bool On, string? Text)> LoadingCalls = new();
    public void SetPlayerLoading(bool on, string? text = null, TimeSpan? baseline = null)
        => LoadingCalls.Add((on, text));

    public readonly List<string?> DownloadBadges = new();
    public void SetDownloadBadge(string? text) => DownloadBadges.Add(text);

    public event Action<string>? Command;
    public event Action? PlaySelected;
    public event Action? TogglePlayedRequested;

    public void RaiseCommand(string cmd)        => Command?.Invoke(cmd);
    public void RaisePlaySelected()             => PlaySelected?.Invoke();
    public void RaiseTogglePlayedRequested()    => TogglePlayedRequested?.Invoke();

    public Episode? StartupEpisode { get; private set; }
    public void ShowStartupEpisode(Episode ep, int? volume = null, double? speed = null)
        => StartupEpisode = ep;

    public readonly List<bool> UnplayedHints = new();
    public void SetUnplayedHint(bool on) => UnplayedHints.Add(on);

    public event Action? QuitRequested;
    public event Action? RemoveFeedRequested;
    public event Action? ToggleThemeRequested;
    public event Func<string, Task>? AddFeedRequested;
    public event Func<Task>? RefreshRequested;

    public void RaiseQuitRequested()        => QuitRequested?.Invoke();
    public void RaiseRemoveFeedRequested()  => RemoveFeedRequested?.Invoke();
    public void RaiseToggleThemeRequested() => ToggleThemeRequested?.Invoke();
    public Task RaiseAddFeedRequested(string url) => AddFeedRequested?.Invoke(url) ?? Task.CompletedTask;
    public Task RaiseRefreshRequested()           => RefreshRequested?.Invoke() ?? Task.CompletedTask;

    public event Action<string>? SearchApplied;
    public void RaiseSearchApplied(string q) => SearchApplied?.Invoke(q);

    public event Action? SelectedFeedChanged;
    public event Action? EpisodeSelectionChanged;

    public void RaiseSelectedFeedChanged()    => SelectedFeedChanged?.Invoke();
    public void RaiseEpisodeSelectionChanged() => EpisodeSelectionChanged?.Invoke();

    public int ForceRedrawCount { get; private set; }
    public void ForceRedraw() => ForceRedrawCount++;
    public void SetHistoryLimit(int n) => LastHistoryLimit = n;
}
