using System;
using System.Linq;
using System.Collections.Generic;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;

sealed class Shell
{
    readonly MemoryLogSink _mem;
    bool _useMenuAccent = true;
    bool _playerAtTop = false;

    // ---- Layout-Konstanten ----
    // 0: Titel/Time, 1: Leerzeile, 2: Buttons, 3: (Spacer), 4: Progress
    const int PlayerContentH = 5;                 // <- extra Luft vor Progress
    const int PlayerFrameH   = PlayerContentH + 2;
    const int SidePad        = 1;

    // data state
    List<Episode> _episodes = new();
    List<Feed> _feeds = new();

    // UI
    Window mainWin = null!;
    FrameView feedsFrame = null!;
    View rightPane = null!;
    FrameView statusFrame = null!;
    TabView rightTabs = null!;
    TextView detailsView = null!;
    ListView feedList = null!;
    ListView episodeList = null!;
    SolidProgressBar progress = null!;
    Label titleLabel = null!;
    Label timeLabel = null!;
    Button btnBack10 = null!;
    Button btnPlayPause = null!;
    Button btnFwd10 = null!;
    Button btnVolDown = null!;
    Button btnVolUp = null!;
    Button btnDownload = null!;
    TextField? commandBox;
    TextField? searchBox;
    string? _lastSearch;

    static bool Has(Key k, Key mask) => (k & mask) == mask;
    static Key  BaseKey(Key k) => k & ~(Key.ShiftMask | Key.CtrlMask | Key.AltMask);

    // events (Program wires these)
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

    public Shell(MemoryLogSink mem) { _mem = mem; }

    public void Build()
    {
        var menu = new MenuBar(new MenuBarItem[] {
            new("_File", new MenuItem[]{
                new("_Add Feed (:add URL)", "", () => ShowCommandBox(":add ")),
                new("_Refresh All (:refresh)", "", async () => { if (RefreshRequested != null) await RefreshRequested(); }),
                new("_Quit (Q)", "Q", () => QuitRequested?.Invoke())
            }),
            new("_View", new MenuItem[]{
                new("_Toggle Player Position (Ctrl+P)", "", () => Command?.Invoke(":player toggle"))
            }),
            new("_Help", new MenuItem[]{
                new("_Keys (:h)", "", () => ShowKeysHelp())
            })
        });
        Application.Top.Add(menu);

        mainWin = new Window { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(PlayerFrameH) };
        mainWin.Border.BorderStyle = BorderStyle.None;
        Application.Top.Add(mainWin);

        feedsFrame = new FrameView("Feeds") { X = 0, Y = 0, Width = 30, Height = Dim.Fill() };
        mainWin.Add(feedsFrame);
        feedList = new ListView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        feedList.OpenSelectedItem += _ => PlaySelected?.Invoke();
        feedList.SelectedItemChanged += OnFeedListSelectedChanged;
        feedsFrame.Add(feedList);

        rightPane = new View { X = Pos.Right(feedsFrame), Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        mainWin.Add(rightPane);

        BuildRightTabs();

        statusFrame = new FrameView("Player") {
            X = SidePad,
            Y = Pos.Bottom(mainWin),                 // direkt andocken
            Width  = Dim.Fill(SidePad * 2),
            Height = PlayerFrameH,
            CanFocus = false,
        };
        Application.Top.Add(statusFrame);

        BuildPlayerBar();
        ApplyTheme(true);

        // Key-Handler breit anbinden
        Application.Top.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };
        mainWin.KeyPress         += e => { if (HandleKeys(e)) e.Handled = true; };
        feedsFrame.KeyPress      += e => { if (HandleKeys(e)) e.Handled = true; };
        rightPane.KeyPress       += e => { if (HandleKeys(e)) e.Handled = true; };
        statusFrame.KeyPress     += e => { if (HandleKeys(e)) e.Handled = true; };
        feedList.KeyPress        += e => { if (HandleKeys(e)) e.Handled = true; };

        SetPlayerPlacement(false);
    }

    public void SetPlayerPlacement(bool atTop)
    {
        _playerAtTop = atTop;

        if (_playerAtTop)
        {
            statusFrame.X = SidePad;
            statusFrame.Y = 1;
            statusFrame.Width  = Dim.Fill(SidePad * 2);
            statusFrame.Height = PlayerFrameH;

            mainWin.Y = 1 + PlayerFrameH;
            mainWin.Height = Dim.Fill();
        }
        else
        {
            mainWin.Y = 1;
            mainWin.Height = Dim.Fill(PlayerFrameH);

            statusFrame.X = SidePad;
            statusFrame.Y = Pos.Bottom(mainWin);
            statusFrame.Width  = Dim.Fill(SidePad * 2);
            statusFrame.Height = PlayerFrameH;
        }

        Application.Top.SetNeedsDisplay();
    }

    public void TogglePlayerPlacement() => SetPlayerPlacement(!_playerAtTop);

    void BuildRightTabs()
    {
        rightPane.RemoveAll();

        rightTabs = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        rightPane.Add(rightTabs);
        rightTabs.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };

        var epHost = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        episodeList = new ListView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        episodeList.OpenSelectedItem += _ => PlaySelected?.Invoke();
        episodeList.SelectedItemChanged += _ => {
            ShowDetailsForSelection();
            EpisodeSelectionChanged?.Invoke();
        };

        epHost.Add(episodeList);
        epHost.KeyPress      += e => { if (HandleKeys(e)) e.Handled = true; };
        episodeList.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };

        var detFrame = new FrameView("Shownotes") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        detailsView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        detFrame.Add(detailsView);
        detFrame.KeyPress    += e => { if (HandleKeys(e)) e.Handled = true; };
        detailsView.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };

        rightTabs.AddTab(new TabView.Tab("Episodes", epHost), true);
        rightTabs.AddTab(new TabView.Tab("Details", detFrame), false);
    }

    public int GetSelectedEpisodeIndex()
    {
        if (_episodes.Count == 0) return 0;
        return Math.Clamp(episodeList.SelectedItem, 0, _episodes.Count - 1);
    }

    public void SelectEpisodeIndex(int index)
    {
        if (_episodes.Count == 0) return;
        episodeList.SelectedItem = Math.Clamp(index, 0, _episodes.Count - 1);
        ShowDetailsForSelection();
    }

    void BuildPlayerBar()
    {
        statusFrame.RemoveAll();

        // Zeile 0: Titel & Zeit
        titleLabel = new Label("—") { X = 2, Y = 0, Width = Dim.Fill(34), Height = 1 };
        timeLabel  = new Label("⏸ 00:00 / --:--  (-:--)  Vol 0%  1.0×")
        {
            X = Pos.AnchorEnd(32), Y = 0, Width = 32, Height = 1, TextAlignment = TextAlignment.Right
        };

        // Zeile 1: bewusst frei

        // Zeile 2: Buttons
        btnBack10    = new Button("«10s")       { X = 2, Y = 2 };
        btnPlayPause = new Button("Play ⏵")     { X = Pos.Right(btnBack10) + 2, Y = 2 };
        btnFwd10     = new Button("10s»")       { X = Pos.Right(btnPlayPause) + 2, Y = 2 };
        btnVolDown   = new Button("Vol−")       { X = Pos.Right(btnFwd10) + 4, Y = 2 };
        btnVolUp     = new Button("Vol+")       { X = Pos.Right(btnVolDown) + 2, Y = 2 };
        btnDownload  = new Button("⬇ Download") { X = Pos.Right(btnVolUp) + 4, Y = 2 };

        // Zeile 3: weitere Leerzeile (Spacing vor Progress)

        // Zeile 4: solide Progressbar in Accent-Foreground
        progress = new SolidProgressBar { X = 2, Y = 4, Width = Dim.Fill(2), Height = 1 };
        progress.ColorScheme = MakeProgressScheme();

        btnBack10.Clicked    += () => Command?.Invoke(":seek -10");
        btnFwd10.Clicked     += () => Command?.Invoke(":seek +10");
        btnPlayPause.Clicked += () => Command?.Invoke(":toggle");
        btnVolDown.Clicked   += () => Command?.Invoke(":vol -5");
        btnVolUp.Clicked     += () => Command?.Invoke(":vol +5");
        btnDownload.Clicked  += () => MessageBox.Query("Download", "Downloads later (M5).", "OK");

        statusFrame.Add(titleLabel, timeLabel,
                        btnBack10, btnPlayPause, btnFwd10, btnVolDown, btnVolUp, btnDownload,
                        progress);
    }

    // Track = Base.Normal (Hintergrund), Fill = Menu.HotNormal (Accent-FOREGROUND!)
    ColorScheme MakeProgressScheme() => new ColorScheme {
        Normal    = Colors.Base.Normal,    // Track (wir malen mit ' ' -> Background zählt)
        Focus     = Colors.Base.Focus,
        Disabled  = Colors.Base.Disabled,
        HotNormal = Colors.Menu.HotNormal, // wird für die Füllung (█, also Foreground) benutzt
        HotFocus  = Colors.Menu.HotFocus
    };

    public void SetFeeds(IEnumerable<Feed> feeds, Guid? selectId = null)
    {
        _feeds = feeds.ToList();
        feedList.SetSource(_feeds.Select(f => f.Title).ToList());

        if (_feeds.Count == 0) return;

        var idx = 0;
        if (selectId is Guid gid)
        {
            var j = _feeds.FindIndex(f => f.Id == gid);
            if (j >= 0) idx = j;
        }
        feedList.SelectedItem = idx;
    }

    public Guid? GetSelectedFeedId()
    {
        if (_feeds.Count == 0 || feedList.Source is null) return null;
        var i = Math.Clamp(feedList.SelectedItem, 0, _feeds.Count - 1);
        return _feeds.ElementAtOrDefault(i)?.Id;
    }

    public void SelectFeed(Guid id)
    {
        if (_feeds.Count == 0) return;
        var idx = _feeds.FindIndex(f => f.Id == id);
        if (idx >= 0) feedList.SelectedItem = idx;
    }

    public void RefreshEpisodesForSelectedFeed(IEnumerable<Episode> episodes)
    {
        var fid = GetSelectedFeedId();
        if (fid is Guid id) SetEpisodesForFeed(id, episodes);
    }

    public void SetEpisodesForFeed(Guid feedId, IEnumerable<Episode> episodes)
    {
        var prevId = GetSelectedEpisode()?.Id;

        _episodes = episodes
            .Where(e => e.FeedId == feedId)
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();

        var items = _episodes.Select(EpisodeRow).ToList();
        episodeList.SetSource(items);

        int sel = 0;
        if (prevId is Guid pid)
        {
            var found = _episodes.FindIndex(e => e.Id == pid);
            if (found >= 0) sel = found;
        }

        episodeList.SelectedItem = (items.Count > 0) ? Math.Clamp(sel, 0, items.Count - 1) : 0;
        ShowDetailsForSelection();
    }

    static string EpisodeRow(Episode e)
    {
        long len = e.LengthMs ?? 0;
        long pos = e.LastPosMs ?? 0;
        double r = (len > 0) ? Math.Clamp((double)pos / len, 0, 1) : 0;
        char mark = e.Played ? '✔' : r <= 0.0 ? '○' : r < 0.25 ? '◔' : r < 0.50 ? '◑' : r < 0.75 ? '◕' : '●';
        var date = e.PubDate?.ToString("yyyy-MM-dd") ?? "????-??-??";
        return $"{mark} {date,-10}  {e.Title}";
    }

    public Episode? GetSelectedEpisode()
    {
        if (_episodes.Count == 0) return null;
        var idx = Math.Clamp(episodeList.SelectedItem, 0, _episodes.Count - 1);
        return _episodes.ElementAtOrDefault(idx);
    }

    public void ShowDetails(Episode e)
    {
        var sb = new System.Text.StringBuilder();
        var date = e.PubDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        var title = e.Title ?? "(untitled)";
        sb.AppendLine(title);
        sb.AppendLine(new string('─', Math.Min(title.Length, 60)));
        sb.AppendLine($"Date: {date}");
        if (!string.IsNullOrWhiteSpace(e.AudioUrl))
            sb.AppendLine($"Audio: {e.AudioUrl}");
        sb.AppendLine();
        var notes = e.DescriptionText?.Trim();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes) ? "(no shownotes)" : notes);
        detailsView.Text = sb.ToString();
    }

    void ShowDetailsForSelection()
    {
        var ep = GetSelectedEpisode();
        if (ep != null) ShowDetails(ep);
    }

    public void UpdatePlayerUI(PlayerState s)
    {
        static string F(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        var pos = s.Position;
        var len = s.Length ?? TimeSpan.Zero;

        var posStr = F(pos);
        var lenStr = len == TimeSpan.Zero ? "--:--" : F(len);
        var remStr = len == TimeSpan.Zero ? "--:--" : F((len - pos) < TimeSpan.Zero ? TimeSpan.Zero : (len - pos));
        var icon   = s.IsPlaying ? "▶" : "⏸";

        timeLabel.Text = $"{icon} {posStr} / {lenStr}  (-{remStr})  Vol {s.Volume0_100}%  {s.Speed:0.0}×";
        btnPlayPause.Text = s.IsPlaying ? "Pause ⏸" : "Play ⏵";

        progress.Fraction = (len.TotalMilliseconds > 0)
            ? Math.Clamp((float)(pos.TotalMilliseconds / len.TotalMilliseconds), 0f, 1f)
            : 0f;
    }

    public void SetWindowTitle(string? subtitle)
    {
        titleLabel.Text = string.IsNullOrWhiteSpace(subtitle) ? "—" : subtitle;
    }

    public void ToggleTheme()
    {
        _useMenuAccent = !_useMenuAccent;
        ApplyTheme(_useMenuAccent);
    }

    void ApplyTheme(bool useMenuAccent)
    {
        var scheme = useMenuAccent ? Colors.Menu : Colors.Base;
        Application.Top.ColorScheme = scheme;
        if (mainWin != null)     mainWin.ColorScheme     = scheme;
        if (feedsFrame != null)  feedsFrame.ColorScheme  = scheme;
        if (rightPane != null)   rightPane.ColorScheme   = scheme;
        if (statusFrame != null) statusFrame.ColorScheme = scheme;
        if (feedList != null)    feedList.ColorScheme    = scheme;
        if (episodeList != null) episodeList.ColorScheme = scheme;
        if (commandBox != null)  commandBox.ColorScheme  = scheme;
        if (searchBox != null)   searchBox.ColorScheme   = scheme;

        // Progressbar an Accent anpassen
        if (progress != null) progress.ColorScheme = MakeProgressScheme();
    }

    public void ShowKeysHelp()
    {
        MessageBox.Query("Keys",
            @"Vim-like keys:
  m mark played/unplayed
  h/l focus pane     j/k move
  Enter play
  / search (Enter apply, n repeat)
  : commands (:add URL, :refresh, :q, :h, :logs, :seek, :vol, :speed)
  u toggle unplayed filter
  J/K next/prev unplayed (play)
Playback:
  Space toggle
  ←/→ -10s/+10s   H/L -60s/+60s
  g/G start/end
  -/+ volume
  [/] slower/faster (= reset 1.0×; 1/2/3 presets)
Misc:
  F12 logs
  t theme toggle
  q quit", "OK");
    }

    public void ShowError(string title, string msg) => MessageBox.ErrorQuery(title, msg, "OK");

    public void ShowLogsOverlay(int tail = 500)
    {
        try
        {
            var lines = _mem.Snapshot(tail);
            var dlg = new Dialog($"Logs (last {tail}) — F12/Esc to close", 100, 30);

            var tv = new TextView {
                ReadOnly = true,
                X = 0, Y = 0,
                Width = Dim.Fill(), Height = Dim.Fill(),
                WordWrap = false
            };
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

    public void RequestAddFeed(string url) => _ = AddFeedRequested?.Invoke(url);
    public void RequestRefresh() => _ = RefreshRequested?.Invoke();
    public void RequestQuit() => QuitRequested?.Invoke();

    public void ShowCommandBox(string seed)
    {
        commandBox?.SuperView?.Remove(commandBox);
        commandBox = new TextField(seed) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        commandBox.ColorScheme = Colors.Base;

        commandBox.KeyPress += (View.KeyEventEventArgs k) =>
        {
            if (k.KeyEvent.Key == Key.Enter)
            {
                var cmd = commandBox!.Text.ToString() ?? "";
                commandBox!.SuperView?.Remove(commandBox);
                commandBox = null;
                Command?.Invoke(cmd);
                k.Handled = true;
            }
            else if (k.KeyEvent.Key == Key.Esc)
            {
                commandBox!.SuperView?.Remove(commandBox);
                commandBox = null;
                k.Handled = true;
            }
        };
        Application.Top.Add(commandBox);
        commandBox.SetFocus();
        commandBox.CursorPosition = commandBox.Text.ToString()!.Length;
    }

    public void ShowSearchBox(string seed)
    {
        searchBox?.SuperView?.Remove(searchBox);
        searchBox = new TextField(seed) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        searchBox.ColorScheme = Colors.Base;

        searchBox.KeyPress += (View.KeyEventEventArgs k) =>
        {
            if (k.KeyEvent.Key == Key.Enter)
            {
                var q = searchBox!.Text.ToString()!.TrimStart('/');
                _lastSearch = q;
                searchBox!.SuperView?.Remove(searchBox);
                searchBox = null;
                SearchApplied?.Invoke(q);
                k.Handled = true;
            }
            else if (k.KeyEvent.Key == Key.Esc)
            {
                searchBox!.SuperView?.Remove(searchBox);
                searchBox = null;
                k.Handled = true;
            }
        };
        Application.Top.Add(searchBox);
        searchBox.SetFocus();
        searchBox.CursorPosition = searchBox.Text.ToString()!.Length;
    }

    bool HandleKeys(View.KeyEventEventArgs e)
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

        if (kv == 'u' || kv == 'U') { TogglePlayedRequested?.Invoke(); return true; }

        if (BaseKey(key) == Key.J && Has(key, Key.ShiftMask)) { JumpToNextUnplayed(); return true; }
        if (BaseKey(key) == Key.K && Has(key, Key.ShiftMask)) { JumpToPrevUnplayed(); return true; }
        if (kv == 'J') { JumpToNextUnplayed(); return true; }
        if (kv == 'K') { JumpToPrevUnplayed(); return true; }

        if (key == (Key)(':')) { ShowCommandBox(":"); return true; }
        if (key == (Key)('/')) { ShowSearchBox("/"); return true; }

        if (key == (Key)('h')) { feedList.SetFocus(); return true; }
        if (key == (Key)('l')) { episodeList.SetFocus(); return true; }
        if (key == (Key)('j')) { MoveList(+1); return true; }
        if (key == (Key)('k')) { MoveList(-1); return true; }

        if (kv == 'i' || kv == 'I') { rightTabs.SelectedTab = rightTabs.Tabs.Last(); detailsView.SetFocus(); return true; }
        if (key == Key.Esc && rightTabs.SelectedTab?.Text.ToString() == "Details")
        { rightTabs.SelectedTab = rightTabs.Tabs.First(); episodeList.SetFocus(); return true; }

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

        if (kv == 'd' || kv == 'D') { MessageBox.Query("Download", "Downloads later (M5).", "OK"); return true; }

        if (key == Key.Enter && rightTabs.SelectedTab?.Text.ToString() != "Details")
        { PlaySelected?.Invoke(); return true; }

        if (key == (Key)('n') && !string.IsNullOrEmpty(_lastSearch)) { SearchApplied?.Invoke(_lastSearch!); return true; }

        return false;
    }

    int CurrentEpisodeIndex()
    {
        if (_episodes.Count == 0) return 0;
        return Math.Clamp(episodeList.SelectedItem, 0, _episodes.Count - 1);
    }

    void JumpToNextUnplayed()
    {
        if (_episodes.Count == 0) return;
        var start = CurrentEpisodeIndex();
        for (int step = 1; step <= _episodes.Count; step++)
        {
            int idx = (start + step) % _episodes.Count;
            if (!_episodes[idx].Played)
            {
                episodeList.SelectedItem = idx;
                ShowDetailsForSelection();
                return;
            }
        }
    }

    void JumpToPrevUnplayed()
    {
        if (_episodes.Count == 0) return;
        var start = CurrentEpisodeIndex();
        for (int step = 1; step <= _episodes.Count; step++)
        {
            int idx = (start - step + _episodes.Count) % _episodes.Count;
            if (!_episodes[idx].Played)
            {
                episodeList.SelectedItem = idx;
                ShowDetailsForSelection();
                return;
            }
        }
    }

    void MoveList(int delta)
    {
        var lv = episodeList.HasFocus ? episodeList : feedList;
        if (lv.Source?.Count > 0)
            lv.SelectedItem = Math.Clamp(lv.SelectedItem + delta, 0, lv.Source.Count - 1);
    }

    void OnFeedListSelectedChanged(ListViewItemEventArgs _)
    {
        SelectedFeedChanged?.Invoke();
    }

    // -------- Solide Progressbar: Track = Background, Fill = Accent-FG mit '█' --------
    sealed class SolidProgressBar : View {
        float _fraction;
        public float Fraction {
            get => _fraction;
            set { _fraction = Math.Clamp(value, 0f, 1f); SetNeedsDisplay(); }
        }
        public SolidProgressBar() { Height = 1; }

        public override void Redraw(Rect bounds) {
            // Track (leer, Hintergrundfarbe der Normal-Attribute)
            Driver.SetAttribute(ColorScheme?.Normal ?? Colors.Base.Normal);
            Move(0, 0);
            for (int i = 0; i < bounds.Width; i++) Driver.AddRune(' ');

            // Fill – volle Blöcke in Accent-FG
            var accent = ColorScheme?.HotNormal ?? Colors.Menu.HotNormal;
            Driver.SetAttribute(accent);
            int filled = (int)Math.Round(bounds.Width * Math.Clamp(Fraction, 0f, 1f));
            Move(0, 0);
            for (int i = 0; i < filled; i++) Driver.AddRune('█'); // Foreground = Accent → dein Orange
        }
    }
}
