using System.Globalization;
using Serilog;
using System.Linq;
using Terminal.Gui;
using System.Linq;              // falls noch nicht drin

using StuiPodcast.Core;
using StuiPodcast.Infra;
using KeyArgs = Terminal.Gui.View.KeyEventEventArgs;

class Program {
    // App state
    
    static object? _progressTimerToken;

    static Window mainWin = null!;
    static FrameView feedsFrame = null!;
    static FrameView epsFrame = null!;
    static FrameView statusFrame = null!;
    static bool useMenuAccent = true; // true = MenuBar-Farben überall

    static AppData Data = new();
    static FeedService? Feeds;
    static IPlayer? Player;

    // UI elements
    static ListView feedList = null!;
    static ListView episodeList = null!;
    static ProgressBar progress = null!;
    static Label nowPlaying = null!;
    static TextField? commandBox;
    static TextField? searchBox;

    static int ActivePane = 0; // 0 = feeds, 1 = episodes
    static string? lastSearch;
    static bool _exiting = false;
    
    
    
    

    static void QuitApp()
    {
        if (_exiting) return;
        _exiting = true;

        // Progress-Timer abstöpseln, damit kein UI-Update mehr reinläuft
        try { if (_progressTimerToken != null) Application.MainLoop?.RemoveTimeout(_progressTimerToken); } catch { }

        // Player-Events lösen und sofort entsorgen (non-blocking in Player.Dispose)
        try { if (Player != null) Player.StateChanged -= OnPlayerStateChanged; } catch { }
        try { (Player as IDisposable)?.Dispose(); } catch { }

        // Loop stoppen
        Application.RequestStop();

        // Fallback: falls irgendein Thread blockiert → harter Exit nach 500ms
        try {
            Application.MainLoop?.AddTimeout(TimeSpan.FromMilliseconds(500), _ => {
                try { Environment.Exit(0); } catch { }
                return false;
            });
        } catch {
            try { Environment.Exit(0); } catch { }
        }
    }



    static async Task Main() {
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File("stui-podcast.log", rollingInterval: RollingInterval.Day)
        .CreateLogger();

    // Daten & Services
    Data  = await AppStorage.LoadAsync();
    Feeds = new FeedService(Data);

    // Default-Feed beim allerersten Start
    if (Data.Feeds.Count == 0) {
        try {
            await Feeds.AddFeedAsync("https://themadestages.podigee.io/feed/mp3");
        } catch (Exception ex) {
            Log.Warning(ex, "Could not add default feed");
        }
    }

    Player = new LibVlcPlayer();
    Player.StateChanged += OnPlayerStateChanged;

    Application.Init();

    var top = Application.Top;

    // Menü (behält eigenes Scheme)
    var menu = new MenuBar(new MenuBarItem[] {
        new("_File", new MenuItem[]{
            new("_Add Feed (:add URL)", "", () => ShowCommandBox(":add ")),
            new("_Refresh All (:refresh)", "", async () => await RefreshAllAsync()),
            new("_Quit (Q)", "Q", () => QuitApp())
        }),
        new("_Help", new MenuItem[]{
            new("_Keys (:h)", "", () => MessageBox.Query("Keys", KeysHelp, "OK"))
        })
    });
    top.Add(menu);

    // Haupt-Container (kein Rahmen; Rahmen haben die Frames)
    mainWin = new Window() { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2) };
    mainWin.Border.BorderStyle = BorderStyle.None;
    top.Add(mainWin);

    // Linke Box: Feeds
    feedsFrame = new FrameView("Feeds") { X = 0, Y = 0, Width = 30, Height = Dim.Fill() };
    mainWin.Add(feedsFrame);

    feedList = new ListView(Data.Feeds.Select(f => f.Title).ToList()) {
        X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()
    };
    feedList.OpenSelectedItem += _ => { ActivePane = 1; episodeList.SetFocus(); };
    feedList.SelectedItemChanged += _ => UpdateEpisodeList();
    feedsFrame.Add(feedList);

    // Rechte Box: Episodes
    epsFrame = new FrameView("Episodes") { X = Pos.Right(feedsFrame), Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    mainWin.Add(epsFrame);

    episodeList = new ListView(new List<string>()) {
        X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()
    };
    episodeList.OpenSelectedItem += _ => PlaySelectedEpisode();
    epsFrame.Add(episodeList);

    // Unten: Player-Box
    statusFrame = new FrameView("Player") { X = 0, Y = Pos.Bottom(mainWin)-2, Width = Dim.Fill(), Height = 2, CanFocus=false };
    nowPlaying = new Label("⏸ 00:00 / --:--") { X = 2, Y = 0, Width = Dim.Fill(), Height = 1 };
    progress   = new ProgressBar()            { X = 2, Y = 1, Width = Dim.Fill(2), Height = 1 };
    statusFrame.Add(nowPlaying, progress);
    top.Add(statusFrame);

    // Theme anwenden (MenuBar-Farben auf alle Hauptviews)
    ApplyTheme(useMenuAccent: true);

    // Key-Handling (global + in Listen)
    top.KeyPress         += (KeyArgs e) => { if (HandleGlobalKeys(e)) e.Handled = true; };
    feedList.KeyPress    += (KeyArgs e) => { if (HandleGlobalKeys(e)) e.Handled = true; };
    episodeList.KeyPress += (KeyArgs e) => { if (HandleGlobalKeys(e)) e.Handled = true; };

    // Initiale Anzeige
    feedList.SetFocus();
    UpdateEpisodeList();

    // Progress-Tick (Token merken, um ihn beim Quit abzuräumen)
    _progressTimerToken = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ => {
        if (Player != null) UpdatePlayerUI(Player.State);
        return true;
    });


    Application.Run();

    // Geordneter Shutdown (falls QuitApp nicht schon alles erledigt hat)
    try { Player?.Stop(); } catch { }
    try { (Player as IDisposable)?.Dispose(); } catch { }
    try { await AppStorage.SaveAsync(Data); } catch { }
    Application.Shutdown();
}

    static void OnPlayerStateChanged(PlayerState s)
    {
        try {
            Application.MainLoop?.Invoke(() => UpdatePlayerUI(s));
        } catch {
            // beim Shutdown kann MainLoop schon weg sein – ignorieren
        }
    }







    static void UpdatePlayerUI(PlayerState s) {
        var pos = s.Position;
        var len = s.Length ?? TimeSpan.Zero;

        string fmt(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        var posStr = fmt(pos);
        var lenStr = len == TimeSpan.Zero ? "--:--" : fmt(len);
        var icon = s.IsPlaying ? "▶" : "⏸";

        nowPlaying.Text = $"{icon} {posStr} / {lenStr}   Vol {s.Volume0_100}%   {s.Speed:0.0}×";

        if (len.TotalMilliseconds > 0) {
            progress.Fraction = Math.Clamp((float)(pos.TotalMilliseconds / len.TotalMilliseconds), 0f, 1f);
        } else {
            progress.Fraction = 0f;
        }
    }


    static void UpdateEpisodeList() {
        var feed = GetSelectedFeed();
        var items = (feed == null)
            ? new List<string>()
            : Data.Episodes.Where(e => e.FeedId == feed.Id)
                .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                .Select(e => $"{(e.PubDate?.ToString("yyyy-MM-dd") ?? "????-??-??"),-10}  {e.Title}")
                .ToList();
        episodeList.SetSource(items);
    }


    static Feed? GetSelectedFeed() {
        if (Data.Feeds.Count == 0) return null;
        var idx = Math.Clamp(feedList.SelectedItem, 0, Data.Feeds.Count-1);
        return Data.Feeds.ElementAtOrDefault(idx);
    }

    static Episode? GetSelectedEpisode() {
        var feed = GetSelectedFeed();
        if (feed == null) return null;
        var episodes = Data.Episodes.Where(e => e.FeedId == feed.Id)
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();
        var idx = Math.Clamp(episodeList.SelectedItem, 0, episodes.Count-1);
        return episodes.ElementAtOrDefault(idx);
    }

    static async Task RefreshAllAsync() {
        if (Feeds == null) return;
        try {
            await Feeds.RefreshAllAsync();
            UpdateEpisodeList();
        } catch (Exception ex) {
            Log.Error(ex, "Refresh failed");
            MessageBox.ErrorQuery("Error", ex.Message, "OK");
        }
    }

    static void PlaySelectedEpisode() {
        var ep = GetSelectedEpisode();
        if (ep == null || Player == null) return;

        Player.Play(ep.AudioUrl);

        // Titel im Window-Title spiegeln
        if (Application.Top.Subviews.OfType<Window>().FirstOrDefault() is Window w) {
            w.Title = $"stui-podcast — {ep.Title}";
        }
    }


    // ---------- Vim-like global keys via AddKeyHandler ----------
    
     // vorher: static bool HandleGlobalKeys(KeyEvent keyEvent)
    static bool HandleGlobalKeys(KeyArgs e)
{
    var key = e.KeyEvent.Key;
    var kv  = e.KeyEvent.KeyValue; // 'q'/'Q'

    // Quit: q, Q, Ctrl+Q
    if (key == (Key.Q | Key.CtrlMask) || key == Key.Q || kv == 'Q' || kv == 'q') {
        QuitApp();
        return true;
    }

    // Theme-Toggle: 't' (MenuBar-Farben ↔ Base)
    if (kv == 't' || kv == 'T') {
        useMenuAccent = !useMenuAccent;
        ApplyTheme(useMenuAccent);
        return true;
    }

    // ":" und "/"
    if (key == (Key)(':')) { ShowCommandBox(":"); return true; }
    if (key == (Key)('/')) { ShowSearchBox("/"); return true; }

    // Vim-Navigation
    if (key == (Key)('h')) { ActivePane = 0; feedList.SetFocus(); return true; }
    if (key == (Key)('l')) { ActivePane = 1; episodeList.SetFocus(); return true; }
    if (key == (Key)('j')) {
        var lv = ActivePane == 0 ? feedList : episodeList;
        lv.SelectedItem = Math.Min(lv.SelectedItem + 1, (lv.Source?.Count ?? 1) - 1);
        if (ActivePane == 0) UpdateEpisodeList();
        return true;
    }
    if (key == (Key)('k')) {
        var lv = ActivePane == 0 ? feedList : episodeList;
        lv.SelectedItem = Math.Max(lv.SelectedItem - 1, 0);
        if (ActivePane == 0) UpdateEpisodeList();
        return true;
    }

    // Playback
    if (key == Key.Space) { Player?.TogglePause(); return true; }
    if (key == Key.CursorLeft || key == (Key)('H')) { Player?.SeekRelative(TimeSpan.FromSeconds(-10)); return true; }
    if (key == Key.CursorRight|| key == (Key)('L')) { Player?.SeekRelative(TimeSpan.FromSeconds(+10)); return true; }
    if (key == (Key)('-')) { if (Player != null) Player.SetVolume(Player.State.Volume0_100 - 5); return true; }
    if (key == (Key)('+')) { if (Player != null) Player.SetVolume(Player.State.Volume0_100 + 5); return true; }
    if (key == (Key)('[')) { if (Player != null) Player.SetSpeed(Player.State.Speed - 0.1); return true; }
    if (key == (Key)(']')) { if (Player != null) Player.SetSpeed(Player.State.Speed + 0.1); return true; }
    if (key == (Key)('=')) { if (Player != null) Player.SetSpeed(1.0); return true; }

    // Enter → Play
    if (key == Key.Enter) { PlaySelectedEpisode(); return true; }

    // Suche wiederholen
    if (key == (Key)('n')) { if (!string.IsNullOrEmpty(lastSearch)) ApplySearch(lastSearch!); return true; }

    return false;
}

    static void ApplyTheme(bool useMenuAccent)
    {
        var scheme = useMenuAccent ? Colors.Menu : Colors.Base;

        try {
            Application.Top.ColorScheme = scheme;
            if (mainWin != null)     mainWin.ColorScheme     = scheme;
            if (feedsFrame != null)  feedsFrame.ColorScheme  = scheme;
            if (epsFrame != null)    epsFrame.ColorScheme    = scheme;
            if (statusFrame != null) statusFrame.ColorScheme = scheme;

            if (feedList != null)    feedList.ColorScheme    = scheme;
            if (episodeList != null) episodeList.ColorScheme = scheme;

            if (commandBox != null)  commandBox.ColorScheme  = scheme;
            if (searchBox != null)   searchBox.ColorScheme   = scheme;
        } catch {
            // falls während Shutdown aufgerufen
        }
    }





    // ":" commandline
    static void ShowCommandBox(string seed) {
        commandBox?.SuperView?.Remove(commandBox);
        commandBox = new TextField(seed) {
            X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1
        };
        commandBox.ColorScheme = Colors.Base;

        commandBox.KeyPress += (KeyArgs k) => {
            if (k.KeyEvent.Key == Key.Enter) {
                var cmd = commandBox!.Text.ToString() ?? "";
                commandBox!.SuperView?.Remove(commandBox);
                commandBox = null;
                HandleCommand(cmd);
                k.Handled = true;
            } else if (k.KeyEvent.Key == Key.Esc) {
                commandBox!.SuperView?.Remove(commandBox);
                commandBox = null;
                k.Handled = true;
            }
        };
        Application.Top.Add(commandBox);
        commandBox.SetFocus();
        commandBox.CursorPosition = commandBox.Text.ToString()!.Length;
    }


    static async void HandleCommand(string cmd) {
        try {
            if (cmd.StartsWith(":add ")) {
                var url = cmd.Substring(5).Trim();
                if (string.IsNullOrWhiteSpace(url)) return;
                var feed = await Feeds!.AddFeedAsync(url);
                // refresh lists
                feedList.SetSource(Data.Feeds.Select(f => f.Title).ToList());
                feedList.SelectedItem = Data.Feeds.FindIndex(f => f.Id == feed.Id);
                UpdateEpisodeList();
            } else if (cmd.StartsWith(":refresh")) {
                await RefreshAllAsync();
            } else if (cmd is ":q" or ":quit") {
                Application.RequestStop();
            } else if (cmd is ":h" or ":help") {
                MessageBox.Query("Keys", KeysHelp, "OK");
            }
        } catch (Exception ex) {
            MessageBox.ErrorQuery("Command error", ex.Message, "OK");
        }
    }

    // "/" search
    static void ShowSearchBox(string seed) {
        searchBox?.SuperView?.Remove(searchBox);
        searchBox = new TextField(seed) { X=0, Y=Pos.AnchorEnd(1), Width=Dim.Fill(), Height=1 };
        searchBox.ColorScheme = Colors.Base;

        searchBox.KeyPress += (KeyArgs k) => {
            if (k.KeyEvent.Key == Key.Enter) {
                var q = searchBox!.Text.ToString()!.TrimStart('/');
                lastSearch = q;
                searchBox!.SuperView?.Remove(searchBox);
                searchBox = null;
                ApplySearch(q);
                k.Handled = true;
            } else if (k.KeyEvent.Key == Key.Esc) {
                searchBox!.SuperView?.Remove(searchBox);
                searchBox = null;
                k.Handled = true;
            }
        };
        Application.Top.Add(searchBox);
        searchBox.SetFocus();
        searchBox.CursorPosition = searchBox.Text.ToString()!.Length;
    }


    static void ApplySearch(string q) {
        var feed = GetSelectedFeed();
        if (feed == null) return;
        var list = Data.Episodes.Where(e => e.FeedId == feed.Id);
        if (!string.IsNullOrWhiteSpace(q)) {
            list = list.Where(e => (e.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                 || (e.DescriptionText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var items = list.OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                        .Select(e => $"{(e.PubDate?.ToString("yyyy-MM-dd") ?? "????-??-??")}  {e.Title}")
                        .ToList();
        episodeList.SetSource(items);
        episodeList.SelectedItem = 0;
        ActivePane = 1; episodeList.SetFocus();
    }

    static string KeysHelp =>
        @"Vim-like keys:
  h/l        Focus left/right pane
  j/k        Move selection
  Enter      Play selected episode
  /          Search titles+shownotes (Enter to apply, n to repeat)
  :          Command line (e.g. :add URL, :refresh, :q, :h)
Playback:
  Space      Play/Pause
  H/L        Seek -10s/+10s   (Arrow keys Left/Right also)
  - / +      Volume down/up
  [ / ]      Slower/Faster    (= reset 1.0×)
Misc:
  q / Q / Ctrl+Q  Quit
";



}
