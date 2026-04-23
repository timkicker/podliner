using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using Attribute = Terminal.Gui.Attribute;
using StuiPodcast.App.UI.Controls;

namespace StuiPodcast.App.UI;

public sealed class UiShell : IUiShell
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
            EpisodeSelectionChanged?.Invoke();
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
                if (_episodesPane?.Tabs is { } tv)
                {
                    tv.SelectedTab = tv.Tabs.Last();
                    _episodesPane.Details.SetFocus();
                }
            },
            BackFromDetails: () =>
            {
                if (_episodesPane?.Tabs?.SelectedTab?.Text.ToString() == "Details")
                {
                    _episodesPane.Tabs.SelectedTab = _episodesPane.Tabs.Tabs.First();
                    _episodesPane.List.SetFocus();
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
            _episodesPane?.Details
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
    public void SetFeeds(IReadOnlyList<Feed> feeds, Guid? selectId = null)
    {
        if (feeds is null) feeds = Array.Empty<Feed>();

        var viewList = BuildFeedsWithBarrier(feeds);

        _suppressFeedSelectionEvents = true;
        try
        {
            _feeds.Clear();
            _feeds.AddRange(viewList);
            _feedsPane?.SetFeeds(_feeds);

            _episodesPane?.SetFeedsMeta(feeds);

            var FEED_ALL = Guid.Parse("00000000-0000-0000-0000-00000000A11A");

            bool anyRealFeeds = feeds.Count > 0;
            Guid want = selectId
                        ?? (anyRealFeeds
                            ? _feeds.FirstOrDefault(f => f.Id != VirtualFeedsCatalog.Seperator)?.Id ?? FEED_ALL
                            : FEED_ALL);

            int idx = 0;
            var j = _feeds.FindIndex(f => f.Id == want);
            if (j >= 0) idx = j;

            if (_feeds.ElementAtOrDefault(idx)?.Id == VirtualFeedsCatalog.Seperator)
            {
                idx = Math.Clamp(idx + 1, 0, Math.Max(0, _feeds.Count - 1));
                if (_feeds.ElementAtOrDefault(idx)?.Id == VirtualFeedsCatalog.Seperator)
                    idx = 0;
            }

            if (_feeds.Count == 0) idx = 0;

            _feedsPane?.List?.SetSelectedItemIfPresent(idx);
        }
        finally
        {
            _suppressFeedSelectionEvents = false;
        }

        RefreshEpisodesForSelectedFeed(_episodes);
    }

    public Guid? GetSelectedFeedId()
    {
        var id = _feedsPane?.GetSelectedFeedId();
        return (id == VirtualFeedsCatalog.Seperator) ? (Guid?)null : id;
    }

    public void SelectFeed(Guid id) => _feedsPane?.SelectFeed(id);

    public void RefreshEpisodesForSelectedFeed(IEnumerable<Episode> episodes)
    {
        var fid = GetSelectedFeedId();
        if (fid is Guid id) SetEpisodesForFeed(id, episodes);
    }

    public void SetEpisodesForFeed(Guid feedId, IEnumerable<Episode> episodes)
    {
        UI(() =>
        {
            if (_episodesPane == null) return;

            _episodesPane.ConfigureFeedColumn(feedId, VirtualFeedsCatalog.All, VirtualFeedsCatalog.Saved, VirtualFeedsCatalog.Downloaded, VirtualFeedsCatalog.History, VirtualFeedsCatalog.Queue);

            var prevId  = _episodesPane.GetSelected()?.Id;
            var keepTop = _episodesPane.List?.TopItem ?? 0;

            _episodesPane.SetEpisodes(
                episodes, feedId,
                VirtualFeedsCatalog.All, VirtualFeedsCatalog.Saved, VirtualFeedsCatalog.Downloaded, VirtualFeedsCatalog.History, VirtualFeedsCatalog.Queue,
                EpisodeSorter, _lastSearch, prevId
            );

            _episodesPane.InjectNowPlaying(_nowPlayingId);

            if (_episodesPane.List?.Source is IList<object> src)
            {
                var maxTop = Math.Max(0, src.Count - 1);
                _episodesPane.List.TopItem = Math.Clamp(keepTop, 0, maxTop);
            }
        });
    }
    
    
    public void RequestRemoveFeed()
    {
        try { RemoveFeedRequested?.Invoke(); } catch { }
    }

    public Episode? GetSelectedEpisode() => _episodesPane?.GetSelected();
    public int GetSelectedEpisodeIndex() => _episodesPane?.GetSelectedIndex() ?? -1;

    public void SelectEpisodeIndex(int index)
    {
        UI(() =>
        {
            if (_episodesPane == null) return;
            _episodesPane.SelectIndex(index);
            var ep = _episodesPane.GetSelected();
            if (ep != null) _episodesPane.ShowDetails(ep);
            if (_episodesPane.List is { } lv) RefreshListVisual(lv);
        });
    }

    public void SetUnplayedHint(bool on) => _episodesPane?.SetUnplayedCaption(on);

    // Forward a fresh playback snapshot to the episodes pane so the active row
    // shows live progress without triggering a full list rebuild.
    public void RefreshActiveProgress(PlaybackSnapshot snap)
        => UI(() => _episodesPane?.RefreshActiveProgress(snap));

    public void SetNowPlaying(Guid? episodeId)
    {
        UI(() =>
        {
            _nowPlayingId = episodeId;
            _episodesPane?.InjectNowPlaying(_nowPlayingId);
        });
    }

    public void ShowStartupEpisode(Episode ep, int? volume = null, double? speed = null)
    {
        UI(() =>
        {
            _startupPinned = true;
            _nowPlayingId = ep.Id;

            SetWindowTitle(ep.Title);
            long len = ep.DurationMs;
            long pos = ep.Progress.LastPosMs;
            if (_player != null)
                _player.Progress.Fraction = (len > 0) ? Math.Clamp((float)pos / len, 0f, 1f) : 0f;

            var lenTs = TimeSpan.FromMilliseconds(Math.Max(0, len));
            var posTs = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(pos, len)));
            var posStr = UIGlyphSet.FormatTime(posTs);
            var lenStr = len == 0 ? "--:--" : UIGlyphSet.FormatTime(lenTs);
            var remStr = len == 0 ? "--:--" : UIGlyphSet.FormatTime(lenTs - posTs);
            _player?.TimeLabel?.SetText($"⏸ {posStr} / {lenStr}  (-{remStr})");

            _episodesPane?.InjectNowPlaying(_nowPlayingId);
        });
    }
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
    public void SetPlayerPlacement(bool atTop)
    {
        UI(() =>
        {
            if (_player == null || _mainWin == null) return;

            _playerAtTop = atTop;

            if (_playerAtTop)
            {
                _player.X = 1;
                _player.Y = 1;
                _mainWin.Y = 1 + UiPlayerPanel.PlayerFrameH;
                _mainWin.Height = Dim.Fill();
            }
            else
            {
                _mainWin.Y = 1;
                _mainWin.Height = Dim.Fill(UiPlayerPanel.PlayerFrameH);
                _player.X = 1;
                _player.Y = Pos.Bottom(_mainWin);
            }

            RequestRepaint();
        });
    }

    public void SetDownloadBadge(string? text)
    {
        try
        {
            Terminal.Gui.Application.MainLoop?.Invoke(() =>
            {
                if (_dlBadge == null) return;

                var show = !string.IsNullOrWhiteSpace(text);
                _dlBadge.Visible = show;
                _dlBadge.Text = show ? text : string.Empty;

                _dlBadge.SetNeedsDisplay();
                Application.Top?.SetNeedsDisplay();
            });
        }
        catch { }
    }

    public void TogglePlayerPlacement() => SetPlayerPlacement(!_playerAtTop);
    public void ShowDetails(Episode e) => UI(() => _episodesPane?.ShowDetails(e));

    private void ApplyTheme()
    {
        UI(() =>
        {
            if (_theme == ThemeMode.User)
            {
                var u = UiShellColorSchemes.BuildUserSchemes();

                if (Application.Top != null)          Application.Top.ColorScheme = u.Main;
                if (_mainWin != null)                 _mainWin.ColorScheme = u.Main;
                if (_rightRoot != null)               _rightRoot.ColorScheme = u.Main;

                if (_menu != null)                    _menu.ColorScheme = u.Menu;
                if (_dlBadge != null)                 _dlBadge.ColorScheme = u.Menu;

                if (_feedsPane?.Frame != null)        _feedsPane.Frame.ColorScheme = u.Main;
                if (_feedsPane?.List  != null)        _feedsPane.List.ColorScheme  = u.List;

                if (_episodesPane?.Tabs != null)      _episodesPane.Tabs.ColorScheme = u.Main;
                if (_episodesPane?.List != null)      _episodesPane.List.ColorScheme = u.List;

                if (_player != null)
                {
                    _player.ColorScheme          = u.Main;
                    _player.Progress.ColorScheme = new ColorScheme {
                        Normal    = u.Status.Normal,
                        Focus     = u.Status.Focus,
                        HotNormal = u.Menu.HotNormal,
                        HotFocus  = u.Menu.HotFocus,
                        Disabled  = u.Status.Disabled
                    };
                    _player.VolBar.ColorScheme   = _player.Progress.ColorScheme;
                }

                if (_commandBox != null) _commandBox.ColorScheme = u.Input;
                if (_searchBox  != null) _searchBox.ColorScheme  = u.Input;

                if (_episodesPane?.Details != null)
                    _episodesPane.Details.ColorScheme = UiShellColorSchemes.BuildDetailsScheme();

                _osd.ApplyTheme();
                RequestRepaint();
                return;
            }

            ColorScheme scheme = _theme switch
            {
                ThemeMode.MenuAccent => Colors.Menu,
                ThemeMode.Base       => Colors.Base,
                ThemeMode.Native     => BuildNativeScheme(),
                _ => Colors.Base
            };

            if (Application.Top != null) Application.Top.ColorScheme = scheme;

            if (_mainWin != null)                 _mainWin.ColorScheme = scheme;
            if (_feedsPane?.Frame != null)        _feedsPane.Frame.ColorScheme = scheme;
            if (_rightRoot != null)               _rightRoot.ColorScheme = scheme;
            if (_player != null)                  _player.ColorScheme = scheme;
            if (_feedsPane?.List != null)         _feedsPane.List.ColorScheme = scheme;
            if (_episodesPane?.Tabs != null)      _episodesPane.Tabs.ColorScheme = scheme;

            if (_player != null)
            {
                _player.Progress.ColorScheme = MakeProgressScheme();
                _player.VolBar.ColorScheme   = MakeProgressScheme();
            }

            _osd.ApplyTheme();
            if (_commandBox != null) _commandBox.ColorScheme = scheme;
            if (_searchBox  != null) _searchBox.ColorScheme  = scheme;

            RequestRepaint();
        });
    }

    private static ColorScheme BuildNativeScheme()
    {
        return new ColorScheme
        {
            Normal    = Colors.Base.Normal,
            Focus     = Colors.Base.Normal,
            HotNormal = Colors.Base.Normal,
            HotFocus  = Colors.Base.Normal,
            Disabled  = Colors.Base.Disabled
        };
    }

    private ColorScheme MakeProgressScheme()
    {
        if (_theme == ThemeMode.Native)
        {
            return new ColorScheme
            {
                Normal    = Colors.Base.Normal,
                Focus     = Colors.Base.Normal,
                HotNormal = Colors.Base.Normal,
                HotFocus  = Colors.Base.Normal,
                Disabled  = Colors.Base.Disabled
            };
        }
        return new ColorScheme
        {
            Normal    = Colors.Base.Normal,
            Focus     = Colors.Base.Focus,
            Disabled  = Colors.Base.Disabled,
            HotNormal = Colors.Menu.HotNormal,
            HotFocus  = Colors.Menu.HotFocus
        };
    }
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
    private void MoveList(int delta)
    {
        var lv = (_activePane == Pane.Episodes) ? _episodesPane?.List : _feedsPane?.List;
        if (lv?.Source?.Count > 0)
        {
            int target = Math.Clamp(lv.SelectedItem + delta, 0, lv.Source.Count - 1);

            if (_activePane == Pane.Feeds && _feedsPane != null)
            {
                int guard = 3;
                while (guard-- > 0)
                {
                    var feed = _feeds.ElementAtOrDefault(target);
                    if (feed?.Id == VirtualFeedsCatalog.Seperator)
                        target = Math.Clamp(target + Math.Sign(delta), 0, lv.Source.Count - 1);
                    else
                        break;
                }
            }

            lv.SelectedItem = target;
            RefreshListVisual(lv);
        }
    }

    private void FocusPane(Pane p)
    {
        _activePane = p;
        if (p == Pane.Episodes)
        {
            _episodesPane?.List?.SetFocus();
            if (_episodesPane?.List is { } lv) RefreshListVisual(lv);
        }
        else
        {
            _feedsPane?.List?.SetFocus();
            if (_feedsPane?.List is { } lv2) RefreshListVisual(lv2);
        }
    }

    public void SetOfflineLookup(Func<bool> isOffline) => _episodesPane?.SetOfflineLookup(isOffline);

    private void JumpToUnplayed(int direction)
    {
        var list = _episodesPane?.List;
        var srcCount = list?.Source?.Count ?? 0;
        if (srcCount == 0 || list == null || _episodesPane == null) return;

        var current = _episodesPane.GetSelectedIndex();
        for (int step = 1; step <= srcCount; step++)
        {
            int idx = (current + step * Math.Sign(direction) + srcCount) % srcCount;

            var old = list.SelectedItem;
            list.SelectedItem = idx;
            var ep = _episodesPane.GetSelected();
            if (ep != null && !ep.ManuallyMarkedPlayed)
            {
                _episodesPane.SelectIndex(idx);
                var e = _episodesPane.GetSelected();
                if (e != null) _episodesPane.ShowDetails(e);
                return;
            }
            list.SelectedItem = old;
        }
    }
    #endregion

    #region defaults/layout helpers
    private void SetDefaultThemeForOS()
    {
        if (OperatingSystem.IsWindows())
            _theme = ThemeMode.Base;
        else
            _theme = ThemeMode.MenuAccent;
    }

    private void RefreshListVisual(ListView lv)
    {
        try
        {
            var count = lv.Source?.Count ?? 0;
            if (count <= 0) return;

            var sel = Math.Clamp(lv.SelectedItem, 0, count - 1);
            var viewHeight = Math.Max(1, lv.Bounds.Height);
            var top = Math.Clamp(lv.TopItem, 0, Math.Max(0, count - 1));

            if (sel < top) lv.TopItem = sel;
            else if (sel >= top + viewHeight) lv.TopItem = Math.Max(0, sel - viewHeight + 1);
        }
        catch { }

        RequestRepaint();
    }

    private void RequestRepaint()
    {
        Application.Top?.SetNeedsDisplay();
        _mainWin?.SetNeedsDisplay();
        _rightRoot?.SetNeedsDisplay();
        _player?.SetNeedsDisplay();
        _feedsPane?.Frame?.SetNeedsDisplay();
        _feedsPane?.List?.SetNeedsDisplay();
        _episodesPane?.Tabs?.SetNeedsDisplay();
        _episodesPane?.List?.SetNeedsDisplay();
        _episodesPane?.EmptyHint?.SetNeedsDisplay();
    }

    private static IEnumerable<Feed> BuildFeedsWithBarrier(IEnumerable<Feed> realFeeds)
    {
        var virt = new List<Feed>
        {
            new Feed { Id = VirtualFeedsCatalog.All,        Title = "All Episodes" },
            new Feed { Id = VirtualFeedsCatalog.Saved,      Title = "★ Saved" },
            new Feed { Id = VirtualFeedsCatalog.Downloaded, Title = "⬇ Downloaded" },
            new Feed { Id = VirtualFeedsCatalog.Queue,      Title = "⧉ Queue" },
            new Feed { Id = VirtualFeedsCatalog.History,    Title = "⏱ History" },
        };

        var barrier = new Feed { Id = VirtualFeedsCatalog.Seperator, Title = "────────" };

        var reals = (realFeeds ?? Enumerable.Empty<Feed>()).ToList();
        return virt.Concat(new[] { barrier }).Concat(reals);
    }

    public void SetQueueOrder(IReadOnlyList<Guid> ids) => _episodesPane?.SetQueueOrder(ids);
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
file static class ViewExtensions
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
