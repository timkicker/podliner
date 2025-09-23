using System;
using System.Linq;
using System.Collections.Generic;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;

namespace StuiPodcast.App.UI;

public sealed class Shell
{
    // ---- Events/API wie vorher ----
    public Func<IEnumerable<Episode>, IEnumerable<Episode>>? EpisodeSorter { get; set; }
    public event Action? EpisodeSelectionChanged;
    public event Action? QuitRequested;
    public event Func<string, System.Threading.Tasks.Task>? AddFeedRequested;
    public event Func<System.Threading.Tasks.Task>? RefreshRequested;
    public event Action? PlaySelected;
    public event Action? ToggleThemeRequested;
    public event Action? TogglePlayedRequested;
    public event Action<string>? Command;
    public event Action<string>? SearchApplied;
    public event Action? SelectedFeedChanged;

    // ---- Konstanten / virtuelle Feeds ----
    private static readonly Guid FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
    private static readonly Guid FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
    private static readonly Guid FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");

    // ---- State ----
    private readonly MemoryLogSink _mem;
    private bool _useMenuAccent = true;
    private bool _playerAtTop   = false;
    private bool _startupPinned = false;

    private string? _lastSearch  = null;
    private Guid? _nowPlayingId  = null;
    private TimeSpan _lastEffLenTs = TimeSpan.Zero;

    private enum Pane { Feeds, Episodes }
    private Pane _activePane = Pane.Episodes;

    private readonly List<Episode> _episodes = new();
    private readonly List<Feed> _feeds = new();

    // ---- UI-Komponenten ----
    private Window _mainWin = null!;
    private View _rightRoot = null!;
    private PlayerPanel _player = null!;
    private FeedsPane _feedsPane = null!;
    private EpisodesPane _episodesPane = null!;
    private OsdOverlay _osd = new();

    private TextField? _commandBox;
    private TextField? _searchBox;

    public Shell(MemoryLogSink mem) { _mem = mem; }

    // ---- Public helpers: keep API stable ----
    public void SetUnplayedFilterVisual(bool on) => _episodesPane?.SetUnplayedCaption(on);
    public Guid? GetNowPlayingId() => _nowPlayingId;
    public void SetWindowTitle(string? s) => _player.TitleLabel.Text = string.IsNullOrWhiteSpace(s) ? "—" : s;

    public void ToggleTheme()
    {
        _useMenuAccent = !_useMenuAccent;
        ApplyTheme();
    }

    public void RequestAddFeed(string url) => _ = AddFeedRequested?.Invoke(url);
    public void RequestRefresh()           => _ = RefreshRequested?.Invoke();
    public void RequestQuit()              => QuitRequested?.Invoke();

    public void ShowOsd(string text, int ms = 1200) => _osd.Show(text, ms);
    public void IndicateRefresh(bool done = false) => ShowOsd(done ? "Refreshed ✓" : "Refreshing…");

    // ---- Build ----
    public void Build()
{
    // Menü bauen
    var menu = MenuBarFactory.Build(new MenuBarFactory.Callbacks(
        Command: (cmd) => Command?.Invoke(cmd),
        RefreshRequested: RefreshRequested,
        AddFeed: () => ShowCommandBox(":add "),
        Quit: () => QuitRequested?.Invoke(),
        FocusFeeds: () => FocusPane(Pane.Feeds),
        FocusEpisodes: () => FocusPane(Pane.Episodes),
        OpenDetails: () =>
        {
            _episodesPane.Tabs.SelectedTab = _episodesPane.Tabs.Tabs.Last();
            _episodesPane.Details.SetFocus();
        },
        BackFromDetails: () =>
        {
            if (_episodesPane.Tabs.SelectedTab?.Text.ToString() == "Details")
            {
                _episodesPane.Tabs.SelectedTab = _episodesPane.Tabs.Tabs.First();
                _episodesPane.List.SetFocus();
            }
        },
        JumpNextUnplayed: () => JumpToUnplayed(+1),
        JumpPrevUnplayed: () => JumpToUnplayed(-1),
        ShowCommand: () => ShowCommandBox(":"),
        ShowSearch:  () => ShowSearchBox("/"),
        ToggleTheme: () => ToggleThemeRequested?.Invoke()
    ));

    // Menü hinzufügen
    Application.Top.Add(menu);

    // WICHTIG: globalen Key-Router AUCH auf Top & Menu hängen
    BindKeys(Application.Top, menu);

    // Main Window
    _mainWin = new Window
    {
        X = 0,
        Y = 1, // unter der Menüleiste
        Width = Dim.Fill(),
        Height = Dim.Fill(PlayerPanel.PlayerFrameH)
    };
    _mainWin.Border.BorderStyle = BorderStyle.None;
    Application.Top.Add(_mainWin);

    // Feeds
    _feedsPane = new FeedsPane();
    _mainWin.Add(_feedsPane.Frame);

    _feedsPane.SelectedChanged += () =>
    {
        if (_activePane == Pane.Feeds)
            RefreshListVisual(_feedsPane.List);
        SelectedFeedChanged?.Invoke();
    };
    _feedsPane.OpenRequested += () => FocusPane(Pane.Episodes);

    // Right Root + Episodes/Details Tabs
    _rightRoot = new View { X = Pos.Right(_feedsPane.Frame), Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    _mainWin.Add(_rightRoot);

    _episodesPane = new EpisodesPane();
    _rightRoot.Add(_episodesPane.Tabs);

    _episodesPane.OpenSelected += () => PlaySelected?.Invoke();
    _episodesPane.SelectionChanged += () =>
    {
        var ep = _episodesPane.GetSelected();
        if (ep != null) _episodesPane.ShowDetails(ep);
        EpisodeSelectionChanged?.Invoke();
    };

    // Player
    _player = new PlayerPanel();
    _player.ProgressSchemeProvider = MakeProgressScheme;
    _player.WireSeeks(cmd => Command?.Invoke(cmd), () => _lastEffLenTs, s => ShowOsd(s));
    Application.Top.Add(_player);

    ApplyTheme();

// IMPORTANT: bind at top-level too, and bind *all* focusable children we use
    BindKeys(
        Application.Top,          // global fallback (was present in your monolith)
        _mainWin,
        _rightRoot,
        _player,
        _feedsPane?.Frame,
        _feedsPane?.List,
        _episodesPane?.Tabs,
        _episodesPane?.List,      // <- was missing: when the list has focus, we want vim keys + ':'
        _episodesPane?.Details    // <- was missing: allow Esc, ':', '/' inside details TextView
    );


    // Player initial unten
    SetPlayerPlacement(false);

    // Nach erstem Layout auf Episodes fokussieren
    Application.MainLoop.AddIdle(() =>
    {
        FocusPane(Pane.Episodes);
        return false;
    });
    
 

}


    // ---- Feeds/Episodes (API) ----
    public void SetFeeds(IEnumerable<Feed> feeds, Guid? selectId = null)
    {
        _feeds.Clear();
        _feeds.AddRange(PrependVirtual(feeds));
        _feedsPane.SetFeeds(_feeds);
        _episodesPane.SetFeedsMeta(_feeds);

        if (_feeds.Count == 0) return;

        var idx = 0;
        if (selectId is Guid gid)
        {
            var j = _feeds.FindIndex(f => f.Id == gid);
            if (j >= 0) idx = j;
        }
        _feedsPane.List.SelectedItem = idx;

        if (_activePane == Pane.Feeds) RefreshListVisual(_feedsPane.List);
    }

    public Guid? GetSelectedFeedId() => _feedsPane.GetSelectedFeedId();

    public void SelectFeed(Guid id) => _feedsPane.SelectFeed(id);

    public void RefreshEpisodesForSelectedFeed(IEnumerable<Episode> episodes)
    {
        var fid = GetSelectedFeedId();
        if (fid is Guid id) SetEpisodesForFeed(id, episodes);
    }

    public void SetEpisodesForFeed(Guid feedId, IEnumerable<Episode> episodes)
    {
        // Feed-Spaltenmodus setzen
        _episodesPane.ConfigureFeedColumn(feedId, FEED_ALL, FEED_SAVED, FEED_DOWNLOADED);

        // aktuelle Auswahl + Scroll behalten
        var prevId  = _episodesPane.GetSelected()?.Id;
        var keepTop = _episodesPane.List?.TopItem ?? 0;

        _episodesPane.SetEpisodes(
            episodes, feedId,
            FEED_ALL, FEED_SAVED, FEED_DOWNLOADED,
            EpisodeSorter, _lastSearch, prevId
        );

        // Now-Playing-Pfeil injizieren
        _episodesPane.InjectNowPlaying(_nowPlayingId);

        // Scroll-Position restaurieren (sicher clampen)
        if (_episodesPane.List?.Source is IList<object> src)
        {
            var maxTop = Math.Max(0, src.Count - 1);
            _episodesPane.List.TopItem = Math.Clamp(keepTop, 0, maxTop);
        }
    }


    public Episode? GetSelectedEpisode() => _episodesPane.GetSelected();

    public int GetSelectedEpisodeIndex() => _episodesPane.GetSelectedIndex();

    public void SelectEpisodeIndex(int index)
    {
        _episodesPane.SelectIndex(index);
        var ep = _episodesPane.GetSelected();
        if (ep != null) _episodesPane.ShowDetails(ep);
        RefreshListVisual(_episodesPane.List);
    }

    public void SetUnplayedHint(bool on) => _episodesPane.SetUnplayedCaption(on);

    public void SetNowPlaying(Guid? episodeId)
    {
        _nowPlayingId = episodeId;
        _episodesPane.InjectNowPlaying(_nowPlayingId);
    }

    public void ShowStartupEpisode(Episode ep, int? volume = null, double? speed = null)
    {
        _startupPinned = true;
        _nowPlayingId = ep.Id;

        SetWindowTitle(ep.Title);
        long len = ep.LengthMs ?? 0;
        long pos = ep.LastPosMs ?? 0;
        _player.Progress.Fraction = (len > 0) ? Math.Clamp((float)pos / len, 0f, 1f) : 0f;

        static string F(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        var lenTs = TimeSpan.FromMilliseconds(Math.Max(0, len));
        var posTs = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(pos, len)));
        var posStr = F(posTs);
        var lenStr = len == 0 ? "--:--" : F(lenTs);
        var remStr = len == 0 ? "--:--" : F((lenTs - posTs) < TimeSpan.Zero ? TimeSpan.Zero : (lenTs - posTs));
        _player.TimeLabel.Text = $"⏸ {posStr} / {lenStr}  (-{remStr})";

        _episodesPane.InjectNowPlaying(_nowPlayingId);
    }

    // ---- Player UI tick (mit smoothing wie gehabt, hier kurz) ----
    public void UpdatePlayerUI(PlayerState s)
    {
        if (_startupPinned)
        {
            bool meaningless = (s.Length == null || s.Length == TimeSpan.Zero)
                                && s.Position == TimeSpan.Zero
                                && !s.IsPlaying;
            if (meaningless) return;
            _startupPinned = false;
        }

        TimeSpan effLen = s.Length ?? TimeSpan.Zero;
        if (s.Position > effLen) effLen = s.Position;
        _lastEffLenTs = effLen;

        static string F(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        _player.Update(s, effLen, F);
    }

    // ---- Theme/Layout ----
    public void SetPlayerPlacement(bool atTop)
    {
        _playerAtTop = atTop;

        if (_playerAtTop)
        {
            _player.X = 1;
            _player.Y = 1;
            _mainWin.Y = 1 + PlayerPanel.PlayerFrameH;
            _mainWin.Height = Dim.Fill();
        }
        else
        {
            _mainWin.Y = 1;
            _mainWin.Height = Dim.Fill(PlayerPanel.PlayerFrameH);
            _player.X = 1;
            _player.Y = Pos.Bottom(_mainWin);
        }

        RequestRepaint();
    }
    public void TogglePlayerPlacement() => SetPlayerPlacement(!_playerAtTop);

    public void ShowDetails(Episode e) => _episodesPane?.ShowDetails(e);

    
    private void ApplyTheme()
    {
        var scheme = _useMenuAccent ? Colors.Menu : Colors.Base;
        if (Application.Top != null) Application.Top.ColorScheme = scheme;

            if (_mainWin != null)                 _mainWin.ColorScheme = scheme;
            if (_feedsPane?.Frame != null)        _feedsPane.Frame.ColorScheme = scheme;
            if (_rightRoot != null)               _rightRoot.ColorScheme = scheme;
            if (_player != null)                  _player.ColorScheme = scheme;
            if (_feedsPane?.List != null)         _feedsPane.List.ColorScheme = scheme;
            if (_episodesPane?.Tabs != null)      _episodesPane.Tabs.ColorScheme = scheme;

        _player.Progress.ColorScheme = MakeProgressScheme();
        _player.VolBar.ColorScheme   = MakeProgressScheme();
        _osd.ApplyTheme();

        if (_commandBox != null) _commandBox.ColorScheme = Colors.Base;
        if (_searchBox  != null) _searchBox.ColorScheme  = Colors.Base;

        RequestRepaint();
    }

    private ColorScheme MakeProgressScheme() => new()
    {
        Normal    = Colors.Base.Normal,
        Focus     = Colors.Base.Focus,
        Disabled  = Colors.Base.Disabled,
        HotNormal = Colors.Menu.HotNormal,
        HotFocus  = Colors.Menu.HotFocus
    };

    // ---- Command/Search ----
    public void ShowCommandBox(string seed)
    {
        _commandBox?.SuperView?.Remove(_commandBox);
        _commandBox = new TextField(seed)
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, ColorScheme = Colors.Base
        };

        _commandBox.KeyPress += (View.KeyEventEventArgs k) =>
        {
            if (k.KeyEvent.Key == Key.Enter)
            {
                var cmd = _commandBox!.Text.ToString() ?? "";
                if (cmd.Trim().StartsWith(":refresh", StringComparison.OrdinalIgnoreCase))
                    IndicateRefresh(false);

                _commandBox!.SuperView?.Remove(_commandBox);
                _commandBox = null;
                Command?.Invoke(cmd);
                k.Handled = true;
            }
            else if (k.KeyEvent.Key == Key.Esc)
            {
                _commandBox!.SuperView?.Remove(_commandBox);
                _commandBox = null;
                k.Handled = true;
            }
        };
        Application.Top.Add(_commandBox);
        _commandBox.SetFocus();
        _commandBox.CursorPosition = _commandBox.Text.ToString()!.Length;
    }

    public void ShowSearchBox(string seed)
    {
        _searchBox?.SuperView?.Remove(_searchBox);
        _searchBox = new TextField(seed)
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, ColorScheme = Colors.Base
        };

        _searchBox.KeyPress += (View.KeyEventEventArgs k) =>
        {
            if (k.KeyEvent.Key == Key.Enter)
            {
                var q = _searchBox!.Text.ToString()!.TrimStart('/');
                _lastSearch = q;
                _searchBox!.SuperView?.Remove(_searchBox);
                _searchBox = null;

                SearchApplied?.Invoke(q);
                SelectedFeedChanged?.Invoke();
                k.Handled = true;
            }
            else if (k.KeyEvent.Key == Key.Esc)
            {
                _searchBox!.SuperView?.Remove(_searchBox);
                _searchBox = null;
                k.Handled = true;
            }
        };
        Application.Top.Add(_searchBox);
        _searchBox.SetFocus();
        _searchBox.CursorPosition = _searchBox.Text.ToString()!.Length;
    }

    // ---- Keys / Moves ----
    private static bool Has(Key k, Key mask) => (k & mask) == mask;
    private static Key BaseKey(Key k) => k & ~(Key.ShiftMask | Key.CtrlMask | Key.AltMask);

    /// <summary>
    /// Attach our key handler to every view passed in (ignores nulls).
    /// </summary>
    private void BindKeys(params View[] views)
    {
        foreach (var v in views)
        {
            if (v == null) continue;
            v.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };
        }
    }


    private bool HandleKeys(View.KeyEventEventArgs e)
    {
        var key = e.KeyEvent.Key;
        var kv  = e.KeyEvent.KeyValue;

        if ((key & Key.CtrlMask) != 0)
        {
            var baseKey = key & ~Key.CtrlMask;
            if (baseKey == Key.C || baseKey == Key.V || baseKey == Key.X) { e.Handled = true; return true; }
        }

        if (kv == 'm' || kv == 'M') { TogglePlayedRequested?.Invoke(); return true; }
        if (key == Key.F12) { ShowLogsOverlay(500); return true; }
        if (key == (Key.Q | Key.CtrlMask) || key == Key.Q || kv == 'Q' || kv == 'q') { QuitRequested?.Invoke(); return true; }
        if (kv == 't' || kv == 'T') { ToggleThemeRequested?.Invoke(); return true; }
        if (kv == 'u' || kv == 'U') { Command?.Invoke(":filter toggle"); return true; }

        if (BaseKey(key) == Key.J && Has(key, Key.ShiftMask)) { JumpToUnplayed(+1); return true; }
        if (BaseKey(key) == Key.K && Has(key, Key.ShiftMask)) { JumpToUnplayed(-1); return true; }
        if (kv == 'J') { JumpToUnplayed(+1); return true; }
        if (kv == 'K') { JumpToUnplayed(-1); return true; }

        if (key == (Key)(':')) { ShowCommandBox(":"); return true; }
        if (key == (Key)('/')) { ShowSearchBox("/"); return true; }

        // pane cycle / details
        if (key == (Key)('h'))
        {
            if (_episodesPane.Tabs.SelectedTab?.Text.ToString() == "Details")
            { _episodesPane.Tabs.SelectedTab = _episodesPane.Tabs.Tabs.First(); _episodesPane.List.SetFocus(); }
            else FocusPane(Pane.Feeds);
            return true;
        }
        if (key == (Key)('l'))
        {
            if (_activePane == Pane.Feeds) FocusPane(Pane.Episodes);
            else
            {
                if (_episodesPane.Tabs.SelectedTab?.Text.ToString() != "Details")
                { _episodesPane.Tabs.SelectedTab = _episodesPane.Tabs.Tabs.Last(); _episodesPane.Details.SetFocus(); }
            }
            return true;
        }

        if (key == (Key)('j') || key == Key.CursorDown) { MoveList(+1); return true; }
        if (key == (Key)('k') || key == Key.CursorUp)   { MoveList(-1); return true; }

        if (kv == 'i' || kv == 'I') { _episodesPane.Tabs.SelectedTab = _episodesPane.Tabs.Tabs.Last(); _episodesPane.Details.SetFocus(); return true; }
        if (key == Key.Esc && _episodesPane.Tabs.SelectedTab?.Text.ToString() == "Details")
        { _episodesPane.Tabs.SelectedTab = _episodesPane.Tabs.Tabs.First(); _episodesPane.List.SetFocus(); return true; }

        if (key == Key.Space) { Command?.Invoke(":toggle"); return true; }
        if (key == Key.CursorLeft || key == (Key)('H')) { Command?.Invoke(":seek -10"); return true; }
        if (key == Key.CursorRight|| key == (Key)('L')) { Command?.Invoke(":seek +10"); return true; }
        if (kv == 'H') { Command?.Invoke(":seek -60"); return true; }
        if (kv == 'L') { Command?.Invoke(":seek +60"); return true; }

        if (kv == 'g') { Command?.Invoke(":seek 0:00"); return true; }
        if (kv == 'G') { Command?.Invoke(":seek 100%"); return true; }

        if (key == (Key)('-')) { Command?.Invoke(":vol -5"); return true; }
        if (key == (Key)('+')) { Command?.Invoke(":vol +5"); return true; }

        if (key == (Key)('[')) { Command?.Invoke(":speed -0.1"); return true; }
        if (key == (Key)(']')) { Command?.Invoke(":speed +0.1"); return true; }
        if (key == (Key)('=')) { Command?.Invoke(":speed 1.0"); return true; }
        if (kv == '1') { Command?.Invoke(":speed 1.0");  return true; }
        if (kv == '2') { Command?.Invoke(":speed 1.25"); return true; }
        if (kv == '3') { Command?.Invoke(":speed 1.5");  return true; }

        if (kv == 'd' || kv == 'D') { Command?.Invoke(":dl toggle"); return true; }

        if (key == Key.Enter)
        {
            if (_activePane == Pane.Feeds) FocusPane(Pane.Episodes);
            else if (_episodesPane.Tabs.SelectedTab?.Text.ToString() != "Details") PlaySelected?.Invoke();
            return true;
        }

        if (key == (Key)('n') && !string.IsNullOrEmpty(_lastSearch))
        {
            SearchApplied?.Invoke(_lastSearch!);
            SelectedFeedChanged?.Invoke();
            return true;
        }

        return false;
    }

    private void MoveList(int delta)
    {
        var lv = (_activePane == Pane.Episodes) ? _episodesPane.List : _feedsPane.List;
        if (lv?.Source?.Count > 0)
        {
            lv.SelectedItem = Math.Clamp(lv.SelectedItem + delta, 0, lv.Source.Count - 1);
            RefreshListVisual(lv);
        }
    }

    private void FocusPane(Pane p)
    {
        _activePane = p;
        if (p == Pane.Episodes)
        {
            _episodesPane.List.SetFocus();
            RefreshListVisual(_episodesPane.List);
        }
        else
        {
            _feedsPane.List.SetFocus();
            RefreshListVisual(_feedsPane.List);
        }
    }

    private void JumpToUnplayed(int direction)
    {
        var list = _episodesPane.List;
        var srcCount = list?.Source?.Count ?? 0;
        if (srcCount == 0) return;

        var current = _episodesPane.GetSelectedIndex();
        for (int step = 1; step <= srcCount; step++)
        {
            int idx = (current + step * Math.Sign(direction) + srcCount) % srcCount;
            // Zugriff auf _episodes: EpisodesPane hält es intern.
            // Wir nehmen das aktuell gewählte Episode-Objekt zum Vergleich:
            // Dazu kleine Hilfsstrategie: Auswahl bewegen & prüfen.
            var old = list.SelectedItem;
            list.SelectedItem = idx;
            var ep = _episodesPane.GetSelected();
            if (ep != null && !ep.Played)
            {
                _episodesPane.SelectIndex(idx);
                var e = _episodesPane.GetSelected();
                if (e != null) _episodesPane.ShowDetails(e);
                return;
            }
            list.SelectedItem = old;
        }
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

    private static IEnumerable<Feed> PrependVirtual(IEnumerable<Feed> feeds)
    {
        var virt = new List<Feed>
        {
            new Feed { Id = FEED_ALL,        Title = "All Episodes" },
            new Feed { Id = FEED_SAVED,      Title = "★ Saved" },
            new Feed { Id = FEED_DOWNLOADED, Title = "⬇ Downloaded" },
        };
        return virt.Concat(feeds ?? Enumerable.Empty<Feed>());
    }

    // ---- Help & Logs (öffentliche API unverändert) ----
    public void ShowKeysHelp() => HelpBrowserDialog.Show();
    public void ShowError(string title, string msg) => MessageBox.ErrorQuery(title, msg, "OK");
    public void ShowLogsOverlay(int tail = 500)
    {
        try
        {
            var lines = _mem.Snapshot(tail);
            var dlg = new Dialog($"Logs (last {tail}) — F12/Esc to close", 100, 30);
            var tv = new TextView { ReadOnly = true, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = false };
            tv.Text = string.Join('\n', lines);
            tv.MoveEnd();

            dlg.KeyPress += (View.KeyEventEventArgs e) =>
            {
                if (e.KeyEvent.Key == Key.F12 || e.KeyEvent.Key == Key.Esc) {
                    Application.RequestStop();
                    e.Handled = true;
                }
            };
            dlg.Add(tv);
            Application.Run(dlg);
        } catch { }
    }
}
