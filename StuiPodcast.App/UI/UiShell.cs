using Serilog;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using Attribute = Terminal.Gui.Attribute;
using StuiPodcast.App.UI.Controls;

namespace StuiPodcast.App.UI;

public sealed partial class UiShell : IUiShell
{
    #region events/api
    // events/api same as before
    public ThemeMode CurrentTheme => _theme;
    public event Action<ThemeMode>? ThemeChanged;

    private bool _suppressFeedSelectionEvents = false;
    private Terminal.Gui.Label? _dlBadge;
    private Terminal.Gui.MenuBar? _menu;

    public Func<IEnumerable<Episode>, IEnumerable<Episode>>? EpisodeSorter { get; set; }
    public event Action? EpisodeSelectionChanged;
    public event Action? QuitRequested;
    public event Action? RemoveFeedRequested;


    public event Func<string, System.Threading.Tasks.Task>? AddFeedRequested;
    public event Func<System.Threading.Tasks.Task>? RefreshRequested;
    public event Action? PlaySelected;
    public event Action? ToggleThemeRequested;
    public event Action? TogglePlayedRequested;
    public event Action<string>? Command;
    public event Action<string>? SearchApplied;
    public event Action? SelectedFeedChanged;
    public event Action<Episode>? ChaptersLoadRequested;

    // Episode id the Chapters tab currently renders — used to reject stale
    // loads that come back after the user already moved on.
    Guid? _chaptersActiveEpisodeId;
    #endregion
    
    #region theme shortcut
    public void SetThemeByNumber(int n)
    {
        var mode = n switch
        {
            1 => ThemeMode.Base,
            2 => ThemeMode.MenuAccent,
            3 => ThemeMode.Native,
            _ => ThemeMode.Base
        };
        _theme = mode;
        ApplyTheme();
    }
    #endregion

    #region state
    private readonly MemoryLogSink _mem;
    private ThemeMode _theme;
    private bool _playerAtTop = false;
    private bool _startupPinned = false;

    private string? _lastSearch = null;
    private Guid? _nowPlayingId = null;
    private TimeSpan _lastEffLenTs = TimeSpan.Zero;

    private enum Pane { Feeds, Episodes }
    private Pane _activePane = Pane.Episodes;

    private readonly List<Episode> _episodes = new();
    private readonly List<Feed> _feeds = new();
    #endregion

    #region ui fields
    private Window? _mainWin;
    private View? _rightRoot;
    private UiPlayerPanel? _player;
    private UiFeedsPane? _feedsPane;
    private UiEpisodesPane? _episodesPane;
    private readonly UiOsdOverlay _osd = new();

    private TextField? _commandBox;
    private TextField? _searchBox;
    #endregion

    #region ctor
    public UiShell(MemoryLogSink mem)
    {
        _mem = mem;
        SetDefaultThemeForOS();
    }
    #endregion

    #region helpers (ui invoke)
    private static void UI(Action a)
    {
        if (Application.MainLoop != null) Application.MainLoop.Invoke(a);
        else a();
    }
    #endregion

    #region public helpers (stable api)
    public void SetUnplayedFilterVisual(bool on) => _episodesPane?.SetUnplayedCaption(on);
    public Guid? GetNowPlayingId() => _nowPlayingId;
    public void SetWindowTitle(string? s) => _player?.TitleLabel?.SetText(string.IsNullOrWhiteSpace(s) ? "—" : s!);

    public void ToggleTheme()
    {
        _theme = _theme switch
        {
            ThemeMode.Base       => ThemeMode.MenuAccent,
            ThemeMode.MenuAccent => ThemeMode.Native,
            ThemeMode.Native     => ThemeMode.User,
            _                    => ThemeMode.Base
        };

        ApplyTheme();
        ThemeChanged?.Invoke(_theme);
    }

    public void SetTheme(ThemeMode mode)
    {
        _theme = mode;
        ApplyTheme();
        ThemeChanged?.Invoke(_theme);
    }

    public void SetQueueLookup(Func<Guid, bool> isQueued) => _episodesPane?.SetQueueLookup(isQueued);

    public void RequestAddFeed(string url) => _ = AddFeedRequested?.Invoke(url);
    public void RequestRefresh()           => _ = RefreshRequested?.Invoke();
    public void RequestQuit()              => QuitRequested?.Invoke();

    public void SetPlayerLoading(bool on, string? text = null, TimeSpan? baseline = null)
    {
        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                _player?.SetLoading(on, text, baseline); 
            });
        }
        catch { }
    }


    public void ShowOsd(string text, int ms = 1200) => _osd.Show(text, ms);
    public void IndicateRefresh(bool done = false)  => ShowOsd(done ? "refreshed ✓" : "refreshing…");

    public void EnsureSelectedFeedVisibleAndTop()
    {
        UI(() =>
        {
            var lv = _feedsPane?.List;
            if (lv == null) return;

            lv.TopItem = 0;
            if (lv.SelectedItem < 0) lv.SelectedItem = 0;
            lv.SetNeedsDisplay();
            lv.SetFocus();
        });
    }
    #endregion

    #region build
    public void Build()
    {
        _mainWin = new Window
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(UiPlayerPanel.PlayerFrameH),
            Border = { BorderStyle = BorderStyle.None }
        };
        Application.Top.Add(_mainWin);

        _feedsPane = new UiFeedsPane();
        _mainWin.Add(_feedsPane.Frame);

        _feedsPane.SelectedChanged += () =>
        {
            if (_suppressFeedSelectionEvents) return;

            var selId = _feedsPane.GetSelectedFeedId();

            if (selId is null && _feedsPane.List is { } lv)
            {
                var next = Math.Min(lv.SelectedItem + 1, (lv.Source?.Count ?? 1) - 1);
                lv.SelectedItem = Math.Max(0, next);
                selId = _feedsPane.GetSelectedFeedId();
                if (selId is null) return;
            }

            if (_activePane == Pane.Feeds)
                RefreshListVisual(_feedsPane.List);

            try { SelectedFeedChanged?.Invoke(); } catch { }
        };

        _rightRoot = new View { X = Pos.Right(_feedsPane.Frame), Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _mainWin.Add(_rightRoot);

        _episodesPane = new UiEpisodesPane();
        _rightRoot.Add(_episodesPane.Tabs);

        _episodesPane.OpenSelected += () => PlaySelected?.Invoke();
        _episodesPane.SelectionChanged += () =>
        {
            var ep = _episodesPane.GetSelected();
            if (ep != null) _episodesPane.ShowDetails(ep);
            // Reset the chapters tab to a placeholder when selection moves
            // so the old episode's chapters don't linger. Actual fetch is
            // deferred to tab-activation — browsing the episode list
            // shouldn't fire network per keypress.
            MaybeResetChaptersForSelection(ep);
            EpisodeSelectionChanged?.Invoke();
        };

        // Lazy-load chapters when the user switches to the Chapters tab.
        _episodesPane.Tabs.SelectedTabChanged += (_, e) =>
        {
            if (e.NewTab == _episodesPane.ChaptersTab)
            {
                var ep = _episodesPane.GetSelected();
                if (ep == null)
                {
                    _episodesPane.ChaptersList.ShowPlaceholder("Select an episode to see chapters");
                    _episodesPane.SetChaptersTabCount(null);
                    return;
                }
                _chaptersActiveEpisodeId = ep.Id;
                _episodesPane.ChaptersList.ShowPlaceholder("Loading chapters…");
                _episodesPane.SetChaptersTabCount(null);
                try { ChaptersLoadRequested?.Invoke(ep); }
                catch (Exception ex) { Log.Debug(ex, "chapters-load-requested subscriber threw"); }
            }
        };

        // Enter on a chapter row → seek to its start. Routed through the
        // command bus so it goes via the same dispatcher as every other
        // user-triggered seek (keeps OSD + status behaviour consistent).
        _episodesPane.ChaptersList.OpenSelected += () =>
        {
            var ch = _episodesPane.ChaptersList.GetSelected();
            if (ch == null) return;
            Command?.Invoke($":seek {(long)ch.StartSeconds}");
            ShowOsd($"▶ {ch.Title}", 1500);
        };

        _player = new UiPlayerPanel();
        _player.ProgressSchemeProvider = MakeProgressScheme;
        _player.WireSeeks(cmd => Command?.Invoke(cmd), () => _lastEffLenTs, s => ShowOsd(s));
        Application.Top.Add(_player);

        _menu = UiMenuBarFactory.Build(new UiMenuBarFactory.Callbacks(
            Command: (cmd) => Command?.Invoke(cmd),
            RefreshRequested: RefreshRequested,
            AddFeed: () => ShowCommandBox(":add "),
            Quit: () => QuitRequested?.Invoke(),
            FocusFeeds: () => FocusPane(Pane.Feeds),
            FocusEpisodes: () => FocusPane(Pane.Episodes),
            OpenDetails: () =>
            {
                if (_episodesPane is { } p)
                {
                    p.Tabs.SelectedTab = p.DetailsTab;
                    p.Details.SetFocus();
                }
            },
            BackFromDetails: () =>
            {
                if (_episodesPane is { } p && p.Tabs.SelectedTab != p.EpisodesTab)
                {
                    p.Tabs.SelectedTab = p.EpisodesTab;
                    p.List.SetFocus();
                }
            },
            JumpNextUnplayed: () => JumpToUnplayed(+1),
            JumpPrevUnplayed: () => JumpToUnplayed(-1),
            ShowCommand: () => ShowCommandBox(":"),                 // plain ':'
            ShowCommandSeeded: seed => ShowCommandBox(seed),        // e.g. ":seek ", ":opml import "
            ToggleTheme: () => ToggleThemeRequested?.Invoke()
        ));
        Application.Top.Add(_menu);


        // compact download badge
        _dlBadge = new Terminal.Gui.Label("")
        {
            Y = 0,
            X = Terminal.Gui.Pos.AnchorEnd(32),
            Width = Terminal.Gui.Dim.Sized(32),
            TextAlignment = Terminal.Gui.TextAlignment.Right,
            Visible = false
        };
        try { _dlBadge.ColorScheme = _menu?.ColorScheme ?? _dlBadge.ColorScheme; } catch { }
        Application.Top.Add(_dlBadge);

        ApplyTheme();

        if (Application.MainLoop != null)
        {
            Application.MainLoop.AddIdle(() =>
            {
                ApplyTheme();
                return false;
            });
        }

        var keyBindings = new UiShellKeyBindings.Bindings(
            EpisodesPane: _episodesPane,
            Player: _player,
            GetSelectedFeedId: GetSelectedFeedId,
            IsFeedsPaneActive: () => _activePane == Pane.Feeds,
            MoveList: MoveList,
            FocusFeeds: () => FocusPane(Pane.Feeds),
            FocusEpisodes: () => FocusPane(Pane.Episodes),
            JumpToUnplayed: JumpToUnplayed,
            InvokeCommand: cmd => Command?.Invoke(cmd),
            TogglePlayed: () => TogglePlayedRequested?.Invoke(),
            ShowLogs: ShowLogsOverlay,
            Quit: () => QuitRequested?.Invoke(),
            ToggleTheme: () => ToggleThemeRequested?.Invoke(),
            ShowCommandBox: ShowCommandBox,
            ShowSearchBox: ShowSearchBox,
            PlaySelected: () => PlaySelected?.Invoke(),
            GetLastSearch: () => _lastSearch,
            ApplySearch: q => SearchApplied?.Invoke(q),
            NotifySelectedFeedChanged: () => SelectedFeedChanged?.Invoke()
        );
        UiShellKeyBindings.Wire(
            keyBindings,
            Application.Top,
            _menu,
            _mainWin,
            _rightRoot,
            _player,
            _feedsPane?.Frame,
            _feedsPane?.List,
            _episodesPane?.Tabs,
            _episodesPane?.List,
            _episodesPane?.Details,
            _episodesPane?.ChaptersList.List
        );

        SetPlayerPlacement(false);

        // Eagerly parent the OSD overlay to the stable root Toplevel now, before any modal
        // dialog can open and cause EnsureCreated() to parent _win to the wrong Toplevel.
        _osd.Initialize(Application.Top);

        Application.MainLoop?.AddIdle(() =>
        {
            FocusPane(Pane.Episodes);
            return false;
        });
    }
    #endregion

    #region feeds/episodes api
    #endregion

    #region player snapshot + legacy tick
    public void UpdatePlayerSnapshot(PlaybackSnapshot snap, int volume0to100)
    {
        UI(() =>
        {
            if (_player == null) return;

            if (_startupPinned)
            {
                bool meaningless = snap.Length == TimeSpan.Zero && snap.Position == TimeSpan.Zero && !snap.IsPlaying;
                if (meaningless) return;
                _startupPinned = false;
            }

            var effLen = snap.Length;
            if (snap.Position > effLen) effLen = snap.Position;
            _lastEffLenTs = effLen;

            _player.Update(snap, volume0to100, UIGlyphSet.FormatTime);
        });
    }

    public void UpdateSpeedEnabled(bool enabled)
    {
        UI(() => _player?.SetSpeedEnabled(enabled));
    }

    // UpdatePlayerUI(PlayerState) was the legacy render path before
    // UpdatePlayerSnapshot replaced it. No caller references it anymore;
    // removed to avoid confusion.
    #endregion

    #region theme/layout
    #endregion

    #region command/search
    public void ShowCommandBox(string seed)
    {
        UI(() =>
        {
            _commandBox?.SuperView?.Remove(_commandBox);
            _commandBox = UiShellPrompts.Show(seed, cmd =>
            {
                _commandBox = null;
                if (cmd.Trim().StartsWith(":refresh", StringComparison.OrdinalIgnoreCase))
                    IndicateRefresh(false);
                Command?.Invoke(cmd);
            });
        });
    }

    public void ShowSearchBox(string seed)
    {
        UI(() =>
        {
            _searchBox?.SuperView?.Remove(_searchBox);
            _searchBox = UiShellPrompts.Show(seed, text =>
            {
                _searchBox = null;
                var q = text.TrimStart('/');
                _lastSearch = q;
                SearchApplied?.Invoke(q);
                SelectedFeedChanged?.Invoke();
            });
        });
    }
    #endregion


    #region downloads/ui lookups
    public void SetDownloadStateLookup(Func<Guid, StuiPodcast.Core.DownloadState> fn)
        => _episodesPane?.SetDownloadStateLookup(fn);

    #endregion

    #region list nav helpers
    #endregion

    #region defaults/layout helpers
    #endregion

    #region help/logs
    public void ShowKeysHelp() => UiHelpBrowserDialog.Show();
    public void ShowError(string title, string msg) => MessageBox.ErrorQuery(title, msg, "OK");

    public Guid AllFeedId => VirtualFeedsCatalog.All;

    public void ScrollEpisodesToTopAndFocus()
    {
        UI(() =>
        {
            _episodesPane?.List?.SetFocus();

            if (_episodesPane?.List != null)
            {
                _episodesPane.List.TopItem = 0;
                _episodesPane.List.SelectedItem = 0;
                _episodesPane.List.SetNeedsDisplay();
            }
        });
    }

    public void ShowLogsOverlay(int tail = 500) => UiShellLogsDialog.Show(_mem, tail);

    public void SetHistoryLimit(int n) => _episodesPane?.SetHistoryLimit(n);
    #endregion
}

// extensions
internal static class ViewExtensions
{
    public static void SetText(this Label? label, string text)
    {
        if (label != null) label.Text = text;
    }

    public static void SetSelectedItemIfPresent(this ListView? lv, int index)
    {
        if (lv?.Source?.Count > 0)
            lv.SelectedItem = Math.Clamp(index, 0, lv.Source.Count - 1);
    }
}