using System;
using System.Linq;
using System.Collections.Generic;
using Terminal.Gui;
using StuiPodcast.App;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;

sealed class Shell
{
    // --------- external wiring ---------
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

    // --------- constants / ids ----------
    private static readonly Guid FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
    private static readonly Guid FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
    private static readonly Guid FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");

    private const int PlayerContentH = 5;  // 0: title/time, 1: empty, 2: controls, 3: spacer, 4: progress
    private const int PlayerFrameH   = PlayerContentH + 2;
    private const int SidePad        = 1;
    private const int FEED_COL_W     = 24;
    private const string SEP         = "  │  ";

    // --------- state ----------
    private readonly MemoryLogSink _mem;
    private bool _useMenuAccent = true;
    private bool _playerAtTop   = false;
    private bool _startupPinned = false;

    private bool _showFeedColumn = false;          // show podcast column on virtual feeds
    private Guid? _nowPlayingId  = null;
    private string? _lastSearch  = null;

    private TimeSpan _lastEffLenTs = TimeSpan.Zero; // for OSD seek target
    private TimeSpan _lastUiPos = TimeSpan.Zero;    // smoothed UI position
    private DateTimeOffset _lastUiAt = DateTimeOffset.MinValue;
    private TimeSpan _lastRawPos = TimeSpan.Zero;   // stall detection
    private DateTimeOffset _lastRawAt = DateTimeOffset.MinValue;

    private enum Pane { Feeds, Episodes }
    private Pane _activePane = Pane.Episodes;

    private List<Episode> _episodes = new();
    private List<Feed> _feeds = new();
    private Dictionary<Guid, string> _feedTitleMap = new(); // cache to avoid per-row lookups

    // --------- UI refs ----------
    private Window mainWin = null!;
    private FrameView feedsFrame = null!;
    private View rightPane = null!;
    private FrameView statusFrame = null!;
    private TabView rightTabs = null!;
    private TextView detailsView = null!;
    private ListView feedList = null!;
    private ListView episodeList = null!;
    private SolidProgressBar progress = null!;
    private Label titleLabel = null!;
    private Label timeLabel = null!;
    private Button btnBack10 = null!;
    private Button btnPlayPause = null!;
    private Button btnFwd10 = null!;
    private Button btnVolDown = null!;
    private Button btnVolUp = null!;
    private Button btnDownload = null!;
    private Button btnSpeedDown = null!;
    private Button btnSpeedUp = null!;
    private Label speedLabel = null!;
    private SolidProgressBar volBar = null!;
    private Label volPctLabel = null!;
    private Label emptyHint = null!;
    private TabView.Tab? _episodesTab;

    // OSD
    private FrameView? _osdWin;
    private Label? _osdLabel;
    private object? _osdTimeout;

    // --------- ctor ----------
    public Shell(MemoryLogSink mem) { _mem = mem; }

    // --------- public small helpers ----------
    public void SetUnplayedFilterVisual(bool on)
    {
        if (_episodesTab != null)
            _episodesTab.Text = on ? "Episodes (Unplayed)" : "Episodes";
    }

    public Guid? GetNowPlayingId() => _nowPlayingId;

    public void SetWindowTitle(string? subtitle)
        => titleLabel.Text = string.IsNullOrWhiteSpace(subtitle) ? "—" : subtitle;

    public void ToggleTheme()
    {
        _useMenuAccent = !_useMenuAccent;
        ApplyTheme(_useMenuAccent);
    }

    public void RequestAddFeed(string url) => _ = AddFeedRequested?.Invoke(url);
    public void RequestRefresh()           => _ = RefreshRequested?.Invoke();
    public void RequestQuit()              => QuitRequested?.Invoke();

    // --------- OSD ----------
    public void ShowOsd(string text, int ms = 1200)
    {
        if (_osdWin == null)
        {
            _osdLabel = new Label("") { X = Pos.Center(), Y = Pos.Center() };
            _osdWin = new FrameView("") {
                Width = 24, Height = 3, CanFocus = false,
                X = Pos.Center(), Y = Pos.Center(),
                ColorScheme = Colors.Menu, Border = { BorderStyle = BorderStyle.Rounded }
            };
            _osdWin.Add(_osdLabel!);
            _osdWin.Visible = false;
            Application.Top.Add(_osdWin);
        }

        _osdLabel!.Text = text;
        _osdWin!.Visible = true;
        RequestRepaint();

        if (_osdTimeout != null)
            try { Application.MainLoop.RemoveTimeout(_osdTimeout); } catch { }

        _osdTimeout = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(ms), _ =>
        {
            _osdWin!.Visible = false;
            RequestRepaint();
            return false;
        });
    }

    public void IndicateRefresh(bool done = false) => ShowOsd(done ? "Refreshed ✓" : "Refreshing…");

    // --------- build UI ----------
    public void Build()
    {
        BuildMenu();

        mainWin = new Window { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(PlayerFrameH) };
        mainWin.Border.BorderStyle = BorderStyle.None;
        Application.Top.Add(mainWin);

        feedsFrame = new FrameView("Feeds") { X = 0, Y = 0, Width = 30, Height = Dim.Fill() };
        mainWin.Add(feedsFrame);

        feedList = new ListView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        feedList.OpenSelectedItem += _ => FocusPane(Pane.Episodes); // no play on double-click
        feedList.SelectedItemChanged += OnFeedListSelectedChanged;
        feedsFrame.Add(feedList);

        rightPane = new View { X = Pos.Right(feedsFrame), Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        mainWin.Add(rightPane);

        BuildRightTabs();

        statusFrame = new FrameView("Player") {
            X = SidePad, Y = Pos.Bottom(mainWin),
            Width = Dim.Fill(SidePad * 2), Height = PlayerFrameH, CanFocus = false,
        };
        Application.Top.Add(statusFrame);

        BuildPlayerBar();
        ApplyTheme(true);

        // global key handlers (keep Vim-ish moves everywhere)
        void bind(View v) => v.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };
        bind(Application.Top);
        bind(mainWin);
        bind(feedsFrame);
        bind(rightPane);
        bind(statusFrame);
        bind(feedList);

        SetPlayerPlacement(false);

        // Focus episodes after first layout
        Application.MainLoop.AddIdle(() =>
        {
            FocusPane(Pane.Episodes);
            return false;
        });
    }

    private void BuildMenu()
    {
        MenuItem Cmd(string text, string help, string cmd)
            => new MenuItem(text, help, () => Command?.Invoke(cmd));
        MenuItem Act(string text, string help, Action act)
            => new MenuItem(text, help, act);

        var menu = new MenuBar(new[]
        {
            new MenuBarItem("_File", new[]
            {
                Act("_Add Feed… (:add URL)", "Open command line with :add",
                    () => ShowCommandBox(":add ")),
                Act("_Refresh All (:refresh)", "Refresh all feeds", async () =>
                {
                    IndicateRefresh(false);
                    if (RefreshRequested != null) await RefreshRequested();
                    IndicateRefresh(true);
                }),
                new MenuItem("-", "", null),
                Act("_Quit (Q)", "Quit application", () => QuitRequested?.Invoke()),
            }),

            new MenuBarItem("_Feeds", new[]
            {
                Cmd("_All Episodes", "", ":feed all"),
                Cmd("_Saved ★",      "", ":feed saved"),
                Cmd("_Downloaded ⬇", "", ":feed downloaded"),
            }),

            new MenuBarItem("_Playback", new[]
            {
                Cmd("_Play/Pause (Space)", "", ":toggle"),
                new MenuItem("-", "", null),
                Cmd("Seek _-10s (←/h/H)", "", ":seek -10"),
                Cmd("Seek _+10s (→/l/L)", "", ":seek +10"),
                Cmd("Seek _-60s (H)",     "", ":seek -60"),
                Cmd("Seek _+60s (L)",     "", ":seek +60"),
                Cmd("Seek _Start (g)",    "", ":seek 0:00"),
                Cmd("Seek _End (G)",      "", ":seek 100%"),
                new MenuItem("-", "", null),
                Cmd("_Speed −0.1 ([)", "", ":speed -0.1"),
                Cmd("S_peed +0.1 (])", "", ":speed +0.1"),
                Cmd("Speed _1.0 (=/1)", "", ":speed 1.0"),
                Cmd("Speed _1.25 (2)",  "", ":speed 1.25"),
                Cmd("Speed _1.5 (3)",   "", ":speed 1.5"),
                new MenuItem("-", "", null),
                Cmd("_Volume −5 (-)", "", ":vol -5"),
                Cmd("Volu_me +5 (+)", "", ":vol +5"),
                new MenuItem("-", "", null),
                Cmd("_Toggle Download Flag (d)", "", ":dl toggle"),
                Act("Toggle _Played (m)", "Mark/Unmark played", () => TogglePlayedRequested?.Invoke()),
            }),

            new MenuBarItem("_View", new[]
            {
                Act("Toggle _Player Position (Ctrl+P)", "Top/bottom player bar",
                    () => Command?.Invoke(":player toggle")),
                Act("Toggle _Theme (t)", "Switch between base/menu accent",
                    () => ToggleThemeRequested?.Invoke()),
                Cmd("Filter: _Unplayed (u)", "", ":filter toggle"),
            }),

            new MenuBarItem("_Navigate", new[]
            {
                Act("Focus _Feeds (h)", "Move focus to feeds",    () => FocusPane(Pane.Feeds)),
                Act("Focus _Episodes (l)", "Move focus to episodes", () => FocusPane(Pane.Episodes)),
                Act("Open _Details (i)", "Switch to details tab", () =>
                {
                    rightTabs.SelectedTab = rightTabs.Tabs.Last(); // "Details"
                    detailsView.SetFocus();
                }),
                Act("_Back from Details (Esc)", "Return to episodes", () =>
                {
                    if (rightTabs.SelectedTab?.Text.ToString() == "Details")
                    {
                        rightTabs.SelectedTab = rightTabs.Tabs.First();
                        episodeList.SetFocus();
                    }
                }),
                new MenuItem("-", "", null),
                Act("Next _Unplayed (J / Shift+j)", "Next unplayed", () => JumpToNextUnplayed()),
                Act("Prev _Unplayed (K / Shift+k)", "Prev unplayed", () => JumpToPrevUnplayed()),
                new MenuItem("-", "", null),
                Act("Open _Command Line (:)", "Open command box", () => ShowCommandBox(":")),
                Act("_Search (/)", "Open search box", () => ShowSearchBox("/")),
            }),

            new MenuBarItem("_Help", new[]
            {
                Act("_Keys & Commands (:h)", "Help browser", () => ShowKeysHelp()),
                Act("_Logs (F12)", "Show logs overlay", () => ShowLogsOverlay(500)),
                Act("_About", "", () =>
                    MessageBox.Query("About", "StuiPodcast: TUI podcast player", "OK")),
            }),
        });

        Application.Top.Add(menu);
    }

    private void BuildRightTabs()
    {
        rightPane.RemoveAll();

        rightTabs = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        rightPane.Add(rightTabs);
        rightTabs.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };

        // Episodes host
        var epHost = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        episodeList = new ListView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        episodeList.OpenSelectedItem += _ => PlaySelected?.Invoke();
        episodeList.SelectedItemChanged += _ => {
            ShowDetailsForSelection();
            EpisodeSelectionChanged?.Invoke();
        };
        episodeList.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };

        emptyHint = new Label("") {
            X = Pos.Center(), Y = Pos.Center(),
            AutoSize = true, Visible = false,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = Colors.Menu
        };

        epHost.Add(episodeList);
        epHost.Add(emptyHint);
        epHost.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };

        var detFrame = new FrameView("Shownotes") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        detailsView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        detFrame.Add(detailsView);
        detFrame.KeyPress    += e => { if (HandleKeys(e)) e.Handled = true; };
        detailsView.KeyPress += e => { if (HandleKeys(e)) e.Handled = true; };

        _episodesTab = new TabView.Tab("Episodes", epHost);
        rightTabs.AddTab(_episodesTab, true);
        rightTabs.AddTab(new TabView.Tab("Details", detFrame), false);
    }

    private void BuildPlayerBar()
    {
        statusFrame.RemoveAll();

        titleLabel = new Label("—") { X = 2, Y = 0, Width = Dim.Fill(34), Height = 1 };
        timeLabel  = new Label("⏸ 00:00 / --:--  (-:--)")
        {
            X = Pos.AnchorEnd(32), Y = 0, Width = 32, Height = 1, TextAlignment = TextAlignment.Right
        };

        const int gapL = 2;
        btnBack10    = new Button("«10s")      { X = 2, Y = 2 };
        btnPlayPause = new Button("Play ⏵")    { X = Pos.Right(btnBack10) + gapL, Y = 2 };
        btnFwd10     = new Button("10s»")      { X = Pos.Right(btnPlayPause) + gapL, Y = 2 };
        btnDownload  = new Button("⬇ Download"){ X = Pos.Right(btnFwd10) + gapL, Y = 2 };

        const int midGap = 2;
        speedLabel   = new Label("1.0×") { Width = 6, Y = 0, X = 0, TextAlignment = TextAlignment.Left };
        btnSpeedDown = new Button("-spd"){ Y = 0, X = Pos.Right(speedLabel) + midGap };
        btnSpeedUp   = new Button("+spd"){ Y = 0, X = Pos.Right(btnSpeedDown) + midGap };
        var midWidth = 6 + midGap + 6 + midGap + 6;
        var mid = new View { Y = 2, X = Pos.Center(), Width = midWidth, Height = 1, CanFocus = false };
        mid.Add(speedLabel, btnSpeedUp, btnSpeedDown);

        const int rightPad = 2;
        const int gap = 2;
        int r = rightPad;

        volBar = new SolidProgressBar { Y = 2, Height = 1, Width = 16, X = Pos.AnchorEnd(r + 16) };
        volBar.ColorScheme = MakeProgressScheme();
        r += 16 + gap - 2;

        volPctLabel = new Label("0%") { Y = 2, Width = 4, X = Pos.AnchorEnd(r + 4), TextAlignment = TextAlignment.Left };
        r += 4 + gap + 1;

        btnVolUp   = new Button("Vol+") { Y = 2, X = Pos.AnchorEnd(r + 6) };
        r += 6 + gap;
        btnVolDown = new Button("Vol−") { Y = 2, X = Pos.AnchorEnd(r + 6) };
        r += 6 + gap;

        progress = new SolidProgressBar { X = 2, Y = 4, Width = Dim.Fill(2), Height = 1 };
        progress.ColorScheme = MakeProgressScheme();

        // progress + volume seeking
        progress.SeekRequested += frac =>
        {
            var pct = (int)Math.Round(Math.Clamp(frac, 0f, 1f) * 100);
            Command?.Invoke($":seek {pct}%");
            if (_lastEffLenTs > TimeSpan.Zero)
            {
                var target = TimeSpan.FromMilliseconds(_lastEffLenTs.TotalMilliseconds * frac);
                ShowOsd($"→ {(int)target.TotalMinutes:00}:{target.Seconds:00}");
            }
        };
        volBar.SeekRequested += frac =>
        {
            var vol = (int)Math.Round(Math.Clamp(frac, 0f, 1f) * 100);
            Command?.Invoke($":vol {vol}");
            ShowOsd($"Vol {vol}%");
        };

        // clicks
        btnBack10.Clicked    += () => Command?.Invoke(":seek -10");
        btnFwd10.Clicked     += () => Command?.Invoke(":seek +10");
        btnPlayPause.Clicked += () => Command?.Invoke(":toggle");
        btnVolDown.Clicked   += () => Command?.Invoke(":vol -5");
        btnVolUp.Clicked     += () => Command?.Invoke(":vol +5");
        btnSpeedDown.Clicked += () => Command?.Invoke(":speed -0.1");
        btnSpeedUp.Clicked   += () => Command?.Invoke(":speed +0.1");
        btnDownload.Clicked  += () => Command?.Invoke(":dl toggle");

        statusFrame.Add(
            titleLabel, timeLabel,
            btnBack10, btnPlayPause, btnFwd10, btnDownload,
            mid, btnVolDown, btnVolUp, volPctLabel, volBar, progress
        );
    }

    // --------- feeds/episodes data ----------
    public void SetFeeds(IEnumerable<Feed> feeds, Guid? selectId = null)
    {
        var virt = new List<Feed>
        {
            new Feed { Id = FEED_ALL,        Title = "All Episodes" },
            new Feed { Id = FEED_SAVED,      Title = "★ Saved" },
            new Feed { Id = FEED_DOWNLOADED, Title = "⬇ Downloaded" },
        };

        _feeds = virt.Concat(feeds ?? Enumerable.Empty<Feed>()).ToList();
        _feedTitleMap = _feeds.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Title ?? "");

        feedList.SetSource(_feeds.Select(f => f.Title).ToList());
        if (_feeds.Count == 0) return;

        var idx = 0;
        if (selectId is Guid gid)
        {
            var j = _feeds.FindIndex(f => f.Id == gid);
            if (j >= 0) idx = j;
        }
        feedList.SelectedItem = idx;

        if (_activePane == Pane.Feeds) RefreshListVisual(feedList);
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
        _showFeedColumn = (feedId == FEED_ALL || feedId == FEED_SAVED || feedId == FEED_DOWNLOADED);

        var prevId = GetSelectedEpisode()?.Id;

        IEnumerable<Episode> src = episodes ?? Enumerable.Empty<Episode>();

        // base filter by feed
        src = feedId switch
        {
            var f when f == FEED_ALL        => src,
            var f when f == FEED_SAVED      => src.Where(IsSaved),
            var f when f == FEED_DOWNLOADED => src.Where(IsDownloaded),
            _                               => src.Where(e => e.FeedId == feedId)
        };

        // apply current search (title/description; also feed title on virtual feeds)
        if (!string.IsNullOrWhiteSpace(_lastSearch))
        {
            var q = _lastSearch!;
            src = src.Where(e =>
                (!string.IsNullOrEmpty(e.Title) && e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.DescriptionText) && e.DescriptionText.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (_showFeedColumn && _feedTitleMap.TryGetValue(e.FeedId, out var ft) &&
                    (!string.IsNullOrEmpty(ft) && ft.Contains(q, StringComparison.OrdinalIgnoreCase)))
            );
        }

        // sort
        src = (EpisodeSorter != null)
            ? EpisodeSorter(src)
            : src.OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue);

        _episodes = src.ToList();

        // selection restore
        int sel = 0;
        if (prevId is Guid pid)
        {
            var found = _episodes.FindIndex(e => e.Id == pid);
            if (found >= 0) sel = found;
        }

        var items = _episodes.Select(EpisodeRow).ToList();
        episodeList.SetSource(items);
        episodeList.SelectedItem = (items.Count > 0) ? Math.Clamp(sel, 0, items.Count - 1) : 0;
        episodeList.TopItem = 0;

        RefreshListVisual(episodeList);
        ShowDetailsForSelection();
        UpdateEmptyHint(feedId);
    }

    public void SetNowPlaying(Guid? episodeId)
    {
        _nowPlayingId = episodeId;
        RebuildEpisodeListPreserveSelection();
    }

    private void RebuildEpisodeListPreserveSelection()
    {
        var sel = Math.Clamp(episodeList.SelectedItem, 0, Math.Max(0, _episodes.Count - 1));
        var items = _episodes.Select(EpisodeRow).ToList();
        episodeList.SetSource(items);
        episodeList.SelectedItem = (items.Count > 0) ? Math.Clamp(sel, 0, items.Count - 1) : 0;
        episodeList.TopItem = 0;
        RefreshListVisual(episodeList);
    }

    private string EpisodeRow(Episode e)
    {
        var now = (_nowPlayingId != null && e.Id == _nowPlayingId.Value);
        var nowPrefix = now ? "▶ " : "  ";

        long lenMs = e.LengthMs ?? 0;
        long posMs = e.LastPosMs ?? 0;
        long effLenMs = Math.Max(lenMs, posMs);
        double r = effLenMs > 0 ? Math.Clamp((double)posMs / effLenMs, 0, 1) : 0;

        char mark = e.Played
            ? '✔'
            : r <= 0.0 ? '○'
                : r < 0.25 ? '◔'
                    : r < 0.50 ? '◑'
                        : r < 0.75 ? '◕'
                            : '●';

        var date = e.PubDate?.ToString("yyyy-MM-dd") ?? "????-??-??";
        string dur = FormatDuration(lenMs);

        char savedCh = (e.Saved == true) ? '★' : ' ';
        char downCh  = (e.Downloaded == true) ? '⬇' : ' ';
        string badges = $"{savedCh}{downCh}";

        string left = $"{nowPrefix}{mark} {date,-10}  {dur,8}  {badges}  ";

        string title = e.Title ?? "";
        int viewWidth = (episodeList?.Bounds.Width > 0) ? episodeList.Bounds.Width : 100;

        // feed column only on virtual feeds
        int reservedRight = _showFeedColumn ? (SEP.Length + FEED_COL_W) : 0;
        int availTitle = Math.Max(6, viewWidth - left.Length - reservedRight);
        string titleTrunc = TruncateTo(title, availTitle);

        if (!_showFeedColumn)
            return left + titleTrunc;

        var feedName = (_feedTitleMap.TryGetValue(e.FeedId, out var nm) ? nm : "") ?? "";
        string feedTrunc = TruncateTo(feedName, FEED_COL_W);
        string paddedTitle = titleTrunc.PadRight(availTitle);

        return left + paddedTitle + SEP + feedTrunc.PadRight(FEED_COL_W);
    }

    private static string TruncateTo(string? s, int max)
    {
        if (max <= 0 || string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        if (max <= 1) return "…";
        return s.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    private static string FormatDuration(long ms)
    {
        if (ms <= 0) return "--:--";
        long totalSeconds = ms / 1000;
        long h = totalSeconds / 3600;
        long m = (totalSeconds % 3600) / 60;
        long s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
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

    private void ShowDetailsForSelection()
    {
        var ep = GetSelectedEpisode();
        if (ep != null) ShowDetails(ep);
    }

    // --------- player ui updates ----------
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

        static string F(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

        var now    = DateTimeOffset.Now;
        var rawPos = s.Position;
        var len    = s.Length ?? TimeSpan.Zero;
        var effLen = rawPos > len ? rawPos : len;

        var forwardJumpTol = TimeSpan.FromMilliseconds(300);
        var backJumpTol    = TimeSpan.FromMilliseconds(300);
        var backJitterTol  = TimeSpan.FromMilliseconds(180);
        var stallWindow    = TimeSpan.FromMilliseconds(90);
        var endCapSlack    = TimeSpan.FromMilliseconds(120);

        bool rawTicked = rawPos != _lastRawPos;
        bool rawStall  = !rawTicked && (_lastRawAt != DateTimeOffset.MinValue) && (now - _lastRawAt) >= stallWindow;

        var pos = _lastUiPos;
        var haveBaseline = _lastUiAt != DateTimeOffset.MinValue;

        if (s.IsPlaying)
        {
            var largeForward = haveBaseline && (rawPos - _lastUiPos) >= forwardJumpTol;
            var largeBackward= haveBaseline && (_lastUiPos - rawPos) >= backJumpTol;

            if (largeForward || largeBackward) pos = rawPos;
            else
            {
                if (rawStall)
                {
                    var wall = now - _lastUiAt;
                    if (wall > TimeSpan.Zero)
                        pos = _lastUiPos + TimeSpan.FromMilliseconds(wall.TotalMilliseconds * s.Speed);
                }
                else
                {
                    if (haveBaseline && rawPos + backJitterTol < _lastUiPos)
                        pos = _lastUiPos;
                    else
                        pos = rawPos;
                }
            }
        }
        else pos = rawPos;

        if (effLen > TimeSpan.Zero && pos > effLen - endCapSlack)
            pos = TimeSpan.FromMilliseconds(Math.Min(pos.TotalMilliseconds, effLen.TotalMilliseconds));

        if (pos > effLen) effLen = pos;

        var icon   = s.IsPlaying ? "▶" : "⏸";
        var posStr = F(pos);
        var lenStr = effLen == TimeSpan.Zero ? "--:--" : F(effLen);
        var rem    = effLen == TimeSpan.Zero ? TimeSpan.Zero : (effLen - pos);
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;

        timeLabel.Text    = $"{icon} {posStr} / {lenStr}  (-{F(rem)})";
        btnPlayPause.Text = s.IsPlaying ? "Pause ⏸" : "Play ⏵";

        progress.Fraction = (effLen.TotalMilliseconds > 0)
            ? Math.Clamp((float)(pos.TotalMilliseconds / effLen.TotalMilliseconds), 0f, 1f)
            : 0f;

        volBar.Fraction  = Math.Clamp(s.Volume0_100 / 100f, 0f, 1f);
        volPctLabel.Text = $"{s.Volume0_100}%";
        speedLabel.Text  = $"{s.Speed:0.0}×";

        _lastUiPos  = pos;
        _lastUiAt   = now;
        _lastRawPos = rawPos;
        _lastRawAt  = now;

        _lastEffLenTs = effLen;
    }

    public void ShowStartupEpisode(Episode ep, int? volume = null, double? speed = null)
    {
        _startupPinned = true;
        _nowPlayingId = ep.Id;

        SetWindowTitle(ep.Title);
        long len = ep.LengthMs ?? 0;
        long pos = ep.LastPosMs ?? 0;
        progress.Fraction = (len > 0) ? Math.Clamp((float)pos / len, 0f, 1f) : 0f;

        static string F(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        var lenTs = TimeSpan.FromMilliseconds(Math.Max(0, len));
        var posTs = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(pos, len)));
        var posStr = F(posTs);
        var lenStr = len == 0 ? "--:--" : F(lenTs);
        var remStr = len == 0 ? "--:--" : F((lenTs - posTs) < TimeSpan.Zero ? TimeSpan.Zero : (lenTs - posTs));
        timeLabel.Text = $"⏸ {posStr} / {lenStr}  (-{remStr})";

        RebuildEpisodeListPreserveSelection();
    }

    // --------- theme / layout ----------
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

        RequestRepaint();
    }

    public void TogglePlayerPlacement() => SetPlayerPlacement(!_playerAtTop);

    private void ApplyTheme(bool useMenuAccent)
    {
        var scheme = useMenuAccent ? Colors.Menu : Colors.Base;

        if (Application.Top != null)
            Application.Top.ColorScheme = scheme;

        if (mainWin != null)     mainWin.ColorScheme     = scheme;
        if (feedsFrame != null)  feedsFrame.ColorScheme  = scheme;
        if (rightPane != null)   rightPane.ColorScheme   = scheme;
        if (statusFrame != null) statusFrame.ColorScheme = scheme;
        if (feedList != null)    feedList.ColorScheme    = scheme;
        if (episodeList != null) episodeList.ColorScheme = scheme;

        // OSD bleibt im Menü-Accent, wie bisher
        if (_osdWin != null) _osdWin.ColorScheme = Colors.Menu;

        // Progressbars ggf. erst nach BuildPlayerBar vorhanden
        if (progress != null) progress.ColorScheme = MakeProgressScheme();
        if (volBar   != null) volBar.ColorScheme   = MakeProgressScheme();

        // <<< Das waren die Crash-Verursacher
        if (commandBox != null) commandBox.ColorScheme = Colors.Base;
        if (searchBox  != null) searchBox.ColorScheme  = Colors.Base;

        RequestRepaint();
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
        RefreshListVisual(episodeList);
        ShowDetailsForSelection();
    }

    public void SetUnplayedHint(bool on)
    {
        if (_episodesTab != null && rightTabs != null)
        {
            _episodesTab.Text = on ? "Episodes (unplayed)" : "Episodes";
            rightTabs.SetNeedsDisplay();
        }
    }

    
    private ColorScheme MakeProgressScheme() => new()
    {
        Normal    = Colors.Base.Normal,
        Focus     = Colors.Base.Focus,
        Disabled  = Colors.Base.Disabled,
        HotNormal = Colors.Menu.HotNormal,
        HotFocus  = Colors.Menu.HotFocus
    };

    // --------- command/search ----------
    private TextField? commandBox;
    private TextField? searchBox;

    public void ShowCommandBox(string seed)
    {
        commandBox?.SuperView?.Remove(commandBox);
        commandBox = new TextField(seed) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, ColorScheme = Colors.Base };

        commandBox.KeyPress += (View.KeyEventEventArgs k) =>
        {
            if (k.KeyEvent.Key == Key.Enter)
            {
                var cmd = commandBox!.Text.ToString() ?? "";
                if (cmd.Trim().StartsWith(":refresh", StringComparison.OrdinalIgnoreCase))
                    IndicateRefresh(false);

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
        searchBox = new TextField(seed) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, ColorScheme = Colors.Base };

        searchBox.KeyPress += (View.KeyEventEventArgs k) =>
        {
            if (k.KeyEvent.Key == Key.Enter)
            {
                var q = searchBox!.Text.ToString()!.TrimStart('/');
                _lastSearch = q;
                searchBox!.SuperView?.Remove(searchBox);
                searchBox = null;

                // notify higher level and trigger immediate reload (fix for All Episodes)
                SearchApplied?.Invoke(q);
                SelectedFeedChanged?.Invoke();

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

    // --------- keys ----------
    private static bool Has(Key k, Key mask) => (k & mask) == mask;
    private static Key BaseKey(Key k) => k & ~(Key.ShiftMask | Key.CtrlMask | Key.AltMask);

    private bool HandleKeys(View.KeyEventEventArgs e)
    {
        var key = e.KeyEvent.Key;
        var kv  = e.KeyEvent.KeyValue;

        // swallow terminal copy/paste on Ctrl+C/V/X (avoid accidental app quit)
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

        if (BaseKey(key) == Key.J && Has(key, Key.ShiftMask)) { JumpToNextUnplayed(); return true; }
        if (BaseKey(key) == Key.K && Has(key, Key.ShiftMask)) { JumpToPrevUnplayed(); return true; }
        if (kv == 'J') { JumpToNextUnplayed(); return true; }
        if (kv == 'K') { JumpToPrevUnplayed(); return true; }

        if (key == (Key)(':')) { ShowCommandBox(":"); return true; }
        if (key == (Key)('/')) { ShowSearchBox("/"); return true; }

        // Vim-style pane cycling & details
        if (key == (Key)('h'))
        {
            if (rightTabs?.SelectedTab?.Text.ToString() == "Details")
            {
                rightTabs.SelectedTab = rightTabs.Tabs.First();
                episodeList.SetFocus();
            }
            else FocusPane(Pane.Feeds);
            return true;
        }
        if (key == (Key)('l'))
        {
            if (_activePane == Pane.Feeds) FocusPane(Pane.Episodes);
            else
            {
                if (rightTabs?.SelectedTab?.Text.ToString() != "Details")
                {
                    rightTabs.SelectedTab = rightTabs.Tabs.Last();
                    detailsView.SetFocus();
                }
            }
            return true;
        }

        if (key == (Key)('j') || key == Key.CursorDown) { MoveList(+1); return true; }
        if (key == (Key)('k') || key == Key.CursorUp)   { MoveList(-1); return true; }

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

        if (kv == 'd' || kv == 'D') { Command?.Invoke(":dl toggle"); return true; }

        if (key == Key.Enter)
        {
            if (_activePane == Pane.Feeds) FocusPane(Pane.Episodes);
            else if (rightTabs.SelectedTab?.Text.ToString() != "Details") PlaySelected?.Invoke();
            return true;
        }

        if (key == (Key)('n') && !string.IsNullOrEmpty(_lastSearch))
        {
            SearchApplied?.Invoke(_lastSearch!);
            SelectedFeedChanged?.Invoke();   // force refresh now
            return true;
        }

        return false;
    }

    // --------- moves / selection ----------
    private int CurrentEpisodeIndex()
    {
        if (_episodes.Count == 0) return 0;
        return Math.Clamp(episodeList.SelectedItem, 0, _episodes.Count - 1);
    }

    private void JumpToNextUnplayed()
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

    private void JumpToPrevUnplayed()
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

    private void MoveList(int delta)
    {
        var lv = (_activePane == Pane.Episodes) ? episodeList : feedList;
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
            episodeList.SetFocus();
            RefreshListVisual(episodeList);
        }
        else
        {
            feedList.SetFocus();
            RefreshListVisual(feedList);
        }
    }

    private void OnFeedListSelectedChanged(ListViewItemEventArgs _)
    {
        if (_activePane == Pane.Feeds) RefreshListVisual(feedList);
        SelectedFeedChanged?.Invoke();
    }

    private void UpdateEmptyHint(Guid feedId)
    {
        bool isEmpty = (_episodes?.Count ?? 0) == 0;

        if (!isEmpty)
        {
            emptyHint.Visible = false;
            emptyHint.Text = "";
            RequestRepaint();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_lastSearch))
        {
            emptyHint.Text = $"No matches for “{_lastSearch}”";
        }
        else if (feedId == FEED_SAVED)
        {
            emptyHint.Text = "No items saved\n(:h for help)";
        }
        else if (feedId == FEED_DOWNLOADED)
        {
            emptyHint.Text = "No items downloaded\n(:h for help)";
        }
        else if (feedId == FEED_ALL)
        {
            emptyHint.Text = "No episodes yet\nAdd one with: :add <rss-url>";
        }
        else
        {
            emptyHint.Text = "No episodes in this feed";
        }

        emptyHint.Visible = true;
        RequestRepaint();
    }

    // make selected visible + repaint cluster
    private void EnsureSelectedVisible(ListView lv)
    {
        var count = lv.Source?.Count ?? 0;
        if (count <= 0) return;

        var sel = Math.Clamp(lv.SelectedItem, 0, count - 1);
        var viewHeight = Math.Max(1, lv.Bounds.Height);
        var top = Math.Clamp(lv.TopItem, 0, Math.Max(0, count - 1));

        if (sel < top) lv.TopItem = sel;
        else if (sel >= top + viewHeight) lv.TopItem = Math.Max(0, sel - viewHeight + 1);
    }

    private void RefreshListVisual(ListView lv)
    {
        try { EnsureSelectedVisible(lv); } catch { }
        RequestRepaint();
    }

    private void RequestRepaint()
    {
        Application.Top?.SetNeedsDisplay();
        mainWin?.SetNeedsDisplay();
        feedsFrame?.SetNeedsDisplay();
        rightPane?.SetNeedsDisplay();
        rightTabs?.SetNeedsDisplay();
        statusFrame?.SetNeedsDisplay();
        episodeList?.SetNeedsDisplay();
        feedList?.SetNeedsDisplay();
        emptyHint?.SetNeedsDisplay();
    }

    // --------- help/logs ----------
    public void ShowKeysHelp() => ShowHelpBrowser();

    public void ShowError(string title, string msg) => MessageBox.ErrorQuery(title, msg, "OK");

    public void ShowLogsOverlay(int tail = 500)
    {
        try
        {
            var lines = _mem.Snapshot(tail);
            var dlg = new Dialog($"Logs (last {tail}) — F12/Esc to close", 100, 30);
            var tv = new TextView {
                ReadOnly = true, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), WordWrap = false
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
        }
        catch { }
    }

    // --------- small helpers ----------
    private static bool IsSaved(Episode e)       => e?.Saved == true;
    private static bool IsDownloaded(Episode e)  => e?.Downloaded == true;

    // --------- inner controls ----------
    private sealed class SolidProgressBar : View
    {
        private float _fraction;
        public float Fraction { get => _fraction; set { _fraction = Math.Clamp(value, 0f, 1f); SetNeedsDisplay(); } }
        public event Action<float>? SeekRequested;

        public SolidProgressBar()
        {
            Height = 1;
            CanFocus = false;
            WantMousePositionReports = true;
        }

        public override void Redraw(Rect bounds)
        {
            Driver.SetAttribute(ColorScheme?.Normal ?? Colors.Base.Normal);
            Move(0, 0);
            for (int i = 0; i < bounds.Width; i++) Driver.AddRune(' ');

            var accent = ColorScheme?.HotNormal ?? Colors.Menu.HotNormal;
            Driver.SetAttribute(accent);
            int filled = (int)Math.Round(bounds.Width * Math.Clamp(Fraction, 0f, 1f));
            Move(0, 0);
            for (int i = 0; i < filled; i++) Driver.AddRune('█');
        }

        public override bool MouseEvent(MouseEvent me)
        {
            bool down = me.Flags.HasFlag(MouseFlags.Button1Clicked)
                        || me.Flags.HasFlag(MouseFlags.Button1Pressed)
                        || me.Flags.HasFlag(MouseFlags.Button1DoubleClicked)
                        || (me.Flags.HasFlag(MouseFlags.ReportMousePosition) && me.Flags.HasFlag(MouseFlags.Button1Pressed));
            if (!down) return base.MouseEvent(me);

            var localX = Math.Clamp(me.X, 0, Math.Max(0, Bounds.Width - 1));
            var width  = Bounds.Width > 1 ? Bounds.Width - 1 : 1;
            var frac = width <= 0 ? 0f : (float)localX / (float)width;

            SeekRequested?.Invoke(frac);
            return true;
        }
    }

    // --------- help browser (unchanged structure; kept compact) ----------
    public void ShowHelpBrowser()
    {
        var dlg = new Dialog("Help — Keys & Commands", 100, 32);
        var tabs = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        dlg.Add(tabs);

        // KEYS
        var keysHost   = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        var keySearch  = new TextField("") { X = 1, Y = 0, Width = Dim.Fill(2) };
        var keyList    = new ListView    { X = 1, Y = 2, Width = 36, Height = Dim.Fill(1) };
        var keyDetails = new TextView    { X = Pos.Right(keyList) + 2, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1), ReadOnly = true, WordWrap = true };
        keysHost.Add(new Label("Search:") { X = 1, Y = 0 }, keySearch, keyList, keyDetails);

        // COMMANDS
        var cmdHost    = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        var cmdSearch  = new TextField("") { X = 1, Y = 0, Width = Dim.Fill(2) };
        var cmdList    = new ListView    { X = 1, Y = 2, Width = 36, Height = Dim.Fill(1) };
        var cmdDetails = new TextView    { X = Pos.Right(cmdList) + 2, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1), ReadOnly = true, WordWrap = true };
        cmdHost.Add(new Label("Search:") { X = 1, Y = 0 }, cmdSearch, cmdList, cmdDetails);

        var keysTab = new TabView.Tab("Keys", keysHost);
        var cmdsTab = new TabView.Tab("Commands", cmdHost);
        tabs.AddTab(keysTab, true);
        tabs.AddTab(cmdsTab, false);

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
        void MoveList(ListView lv, int d) { var c = lv.Source?.Count ?? 0; if (c <= 0) return; lv.SelectedItem = Clamp(lv.SelectedItem + d, 0, c - 1); }
        void GoTop(ListView lv)    { var c = lv.Source?.Count ?? 0; if (c > 0) lv.SelectedItem = 0; }
        void GoBottom(ListView lv) { var c = lv.Source?.Count ?? 0; if (c > 0) lv.SelectedItem = c - 1; }
        void FocusKeysTab() { tabs.SelectedTab = keysTab; keyList.SetFocus(); }
        void FocusCmdsTab() { tabs.SelectedTab = cmdsTab; cmdList.SetFocus(); }

        List<KeyHelp> keyData = HelpCatalog.Keys.ToList();
        List<CmdHelp> cmdData = HelpCatalog.Commands.ToList();

        void RefreshKeyList()
        {
            var q = keySearch.Text.ToString() ?? "";
            var filtered = string.IsNullOrWhiteSpace(q)
                ? keyData
                : keyData.Where(k =>
                        k.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        k.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (k.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();

            keyList.SetSource(filtered.Select(k => k.Key).ToList());
            keyList.SelectedItem = 0;
        }

        void RefreshCmdList()
        {
            var q = cmdSearch.Text.ToString() ?? "";
            var filtered = string.IsNullOrWhiteSpace(q)
                ? cmdData
                : cmdData.Where(c =>
                        c.Command.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        c.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (c.Args?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (c.Aliases?.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false))
                    .ToList();

            cmdList.SetSource(filtered.Select(c => c.Command).ToList());
            cmdList.SelectedItem = 0;
        }

        keySearch.KeyPress += (View.KeyEventEventArgs _) =>
            Application.MainLoop.AddIdle(() => { RefreshKeyList(); return false; });
        cmdSearch.KeyPress += (View.KeyEventEventArgs _) =>
            Application.MainLoop.AddIdle(() => { RefreshCmdList(); return false; });

        keyList.SelectedItemChanged += _ =>
        {
            var src = keyList.Source?.ToList() ?? new List<object>();
            var idx = Math.Clamp(keyList.SelectedItem, 0, src.Count - 1);
            if (src.Count == 0) { keyDetails.Text = ""; return; }

            var name  = src[idx].ToString();
            var item  = HelpCatalog.Keys.FirstOrDefault(k => k.Key == name) ?? keyData.First();
            var notes = string.IsNullOrWhiteSpace(item.Notes) ? "" : $"\n\nNotes: {item.Notes}";
            keyDetails.Text = $"{item.Key}\n\n{item.Description}{notes}";
        };

        cmdList.SelectedItemChanged += _ =>
        {
            var src = cmdList.Source?.ToList() ?? new List<object>();
            var idx = Math.Clamp(cmdList.SelectedItem, 0, src.Count - 1);
            if (src.Count == 0) { cmdDetails.Text = ""; return; }

            var name = src[idx].ToString();
            var item = HelpCatalog.Commands.FirstOrDefault(c => c.Command == name) ?? cmdData.First();

            string aliases  = (item.Aliases is { Length: > 0 }) ? $"Aliases: {string.Join(", ", item.Aliases)}\n" : "";
            string args     = string.IsNullOrWhiteSpace(item.Args) ? "" : $"Args: {item.Args}\n";
            string examples = (item.Examples is { Length: > 0 }) ? $"Examples:\n  - {string.Join("\n  - ", item.Examples)}\n" : "";

            cmdDetails.Text = $"{item.Command}\n\n{item.Description}\n\n{aliases}{args}{examples}".TrimEnd();
        };

        keyList.KeyPress += e =>
        {
            if (keySearch.HasFocus) return;
            var key = e.KeyEvent.Key; var ch = e.KeyEvent.KeyValue;
            if (ch == 'j' || key == Key.CursorDown) { MoveList(keyList, +1); e.Handled = true; return; }
            if (ch == 'k' || key == Key.CursorUp)   { MoveList(keyList, -1); e.Handled = true; return; }
            if (ch == 'g' && (key & Key.ShiftMask) == 0) { GoTop(keyList); e.Handled = true; return; }
            if (ch == 'G') { GoBottom(keyList); e.Handled = true; return; }
            if (ch == '/') { keySearch.SetFocus(); e.Handled = true; return; }
            if (ch == 'h') { FocusKeysTab(); e.Handled = true; return; }
            if (ch == 'l') { FocusCmdsTab(); e.Handled = true; return; }
        };

        cmdList.KeyPress += e =>
        {
            if (cmdSearch.HasFocus) return;
            var key = e.KeyEvent.Key; var ch = e.KeyEvent.KeyValue;
            if (ch == 'j' || key == Key.CursorDown) { MoveList(cmdList, +1); e.Handled = true; return; }
            if (ch == 'k' || key == Key.CursorUp)   { MoveList(cmdList, -1); e.Handled = true; return; }
            if (ch == 'g' && (key & Key.ShiftMask) == 0) { GoTop(cmdList); e.Handled = true; return; }
            if (ch == 'G') { GoBottom(cmdList); e.Handled = true; return; }
            if (ch == '/') { cmdSearch.SetFocus(); e.Handled = true; return; }
            if (ch == 'h') { FocusKeysTab(); e.Handled = true; return; }
            if (ch == 'l') { FocusCmdsTab(); e.Handled = true; return; }
        };

        dlg.KeyPress += e =>
        {
            var ch = e.KeyEvent.KeyValue;
            if (ch == 'h') { FocusKeysTab(); e.Handled = true; return; }
            if (ch == 'l') { FocusCmdsTab(); e.Handled = true; return; }
            if (e.KeyEvent.Key == Key.Esc || e.KeyEvent.Key == Key.F12)
            {
                Application.RequestStop();
                e.Handled = true;
            }
        };

        RefreshKeyList();
        RefreshCmdList();
        Application.Run(dlg);
    }
}
