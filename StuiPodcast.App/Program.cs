using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Terminal.Gui;

using StuiPodcast.Core;
using StuiPodcast.Infra;
using KeyArgs = Terminal.Gui.View.KeyEventEventArgs;

class Program {
    // ---------- App state ----------
    static object? _progressTimerToken;
    // Resume-Logik
    static object? _resumeTimerToken;
    static long?   _resumeWantedMs;
    static int _exitOnce = 0;
    
    static volatile bool _shuttingDown = false;
    static System.Threading.CancellationTokenSource _shutdownCts = new();
    static System.Threading.SemaphoreSlim _saveGate = new(1,1);
    static Task? _inflightSave = null;
    static DateTime _lastPeriodicSave = DateTime.MinValue;
    
    // Autosave-Logik
    static DateTime _lastUiProgressRefresh = DateTime.MinValue; // falls noch nicht da
    static System.Threading.CancellationTokenSource? _saveDebounceCts;

    static bool UseAsciiMarks = false; // mit 'P' togglen, falls Unicode-Kreise nicht hübsch sind

    static Episode? currentEpisode;
    static DateTime _lastProgressPersist = DateTime.MinValue;

    static Window mainWin = null!;
    static FrameView feedsFrame = null!;
    static View epsFrame = null!;
    static FrameView statusFrame = null!;
    static bool useMenuAccent = true; // true = MenuBar-Farben überall

    static AppData Data = new();
    static FeedService? Feeds;
    static IPlayer? Player;
    static bool _exiting = false;

    // ---------- UI elements ----------
    static ListView feedList = null!;
    static ListView episodeList = null!;
    static ProgressBar progress = null!;
    static TextField? commandBox;
    static TextField? searchBox;

    static int ActivePane = 0; // 0 = feeds, 1 = episodes
    static string? lastSearch;

    // Player-UI Controls
    static Label titleLabel = null!;
    static Label timeLabel = null!;
    static Button btnBack10 = null!;
    static Button btnPlayPause = null!;
    static Button btnFwd10 = null!;
    static Button btnVolDown = null!;
    static Button btnVolUp = null!;
    static Button btnDownload = null!;

    // ---------- Logging (Datei + Memory für In-App-Viewer) ----------
    static MemoryLogSink MemLog = new MemoryLogSink(2000);

    static char ProgressGlyph(double p, bool played)
    {
        // Unicode hübsch, ASCII fallback
        if (!UseAsciiMarks) {
            if (played) return '✔';
            if (p <= 0.0) return '○';
            if (p < 0.25) return '◔';
            if (p < 0.50) return '◑';
            if (p < 0.75) return '◕';
            return '●';
        } else {
            if (played) return '#';
            if (p <= 0.0) return ' ';
            if (p < 0.25) return '·';
            if (p < 0.50) return '-';
            if (p < 0.75) return '=';
            return '≡';
        }
    }

    static string EpisodeRowText(Episode e)
    {
        var len = (e.LengthMs ?? 0);
        var pos = (e.LastPosMs ?? 0);
        double ratio = (len > 0) ? Math.Clamp((double)pos / len, 0, 1) : 0;
        var mark = ProgressGlyph(ratio, e.Played);
        var date = e.PubDate?.ToString("yyyy-MM-dd") ?? "????-??-??";
        return $"{mark} {date,-10}  {e.Title}";
    }

    
    static void ConfigureLogging() {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("pid", Environment.ProcessId)
            .WriteTo.Sink(MemLog) // → live im TUI via F12 / :logs
            .WriteTo.File(
                Path.Combine(logDir, "stui-podcast-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Exception}{NewLine}"
            )
            .CreateLogger();
    }

    static void InstallGlobalErrorHandlers() {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "UnhandledException (IsTerminating={Terminating})", e.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (s, e) => {
            Log.Fatal(e.Exception, "UnobservedTaskException");
            e.SetObserved();
        };
    }


    static void QuitApp()
    {
        if (System.Threading.Interlocked.Exchange(ref _exitOnce, 1) == 1) return;

        try { Application.MainLoop?.RemoveTimeout(_progressTimerToken); } catch { }
        try { _saveDebounceCts?.Cancel(); } catch { }

        // Player best effort schließen, NICHT warten
        try { if (Player != null) Player.StateChanged -= OnPlayerStateChanged; } catch { }
        try { Player?.Stop(); } catch { }
        try { (Player as IDisposable)?.Dispose(); } catch { }

        // TUI runterfahren
        try { Application.RequestStop(); } catch { }
        try { Application.Shutdown(); } catch { }

        // TERMINAL HART RESETTEN
        try {
            // Maus-Tracking aus, Bracketed Paste aus, Cursor an, Attrs reset, Alt-Screen raus
            Console.Write(
                "\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l" + // mouse off (alle Modi)
                "\x1b[?2004l" +                                           // bracketed paste off
                "\x1b[?25h"   +                                           // cursor sichtbar
                "\x1b[0m"     +                                           // SGR reset
                "\x1b[?1049l"                                             // alt screen verlassen
            );
            Console.Out.Flush();
            // Voll-Reset (RIS) – falls oben nicht reicht
            Console.Write("\x1bc");
            Console.Out.Flush();
        } catch { }

        try { Log.CloseAndFlush(); } catch { }

        // **jetzt sofort raus**
        Environment.Exit(0);
    }




    // ---------- Main ----------
    static async Task Main() {
    ConfigureLogging();
    InstallGlobalErrorHandlers();
    Log.Information("Startup");

    Data  = await AppStorage.LoadAsync();
    Feeds = new FeedService(Data);
    if (Data.Feeds.Count == 0) {
        try { await Feeds.AddFeedAsync("https://themadestages.podigee.io/feed/mp3"); }
        catch (Exception ex) { Log.Warning(ex, "Could not add default feed"); }
    }

    Player = new LibVlcPlayer();
    Player.StateChanged += OnPlayerStateChanged;

    Console.TreatControlCAsInput = false;                 // Ctrl+C soll SIGINT sein
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; QuitApp(); };
    AppDomain.CurrentDomain.ProcessExit += (_, __) => {   // falls Exit anderswo passiert
        try {
            Console.Write("\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l\x1b[?2004l\x1b[?25h\x1b[0m\x1b[?1049l\x1bc");
            Console.Out.Flush();
        } catch { }
    };

    AppDomain.CurrentDomain.ProcessExit += (_, __) => { ResetTerminal(); };

    Application.Init();
    var top = Application.Top;

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

    // Hauptcontainer (ohne Titelzeile)
    mainWin = new Window { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(4) };
    mainWin.Border.BorderStyle = BorderStyle.None;
    mainWin.Title = "";
    top.Add(mainWin);

    // Feeds links
    feedsFrame = new FrameView("Feeds") { X = 0, Y = 0, Width = 30, Height = Dim.Fill() };
    mainWin.Add(feedsFrame);
    feedList = new ListView(Data.Feeds.Select(f => f.Title).ToList()) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    feedList.OpenSelectedItem += _ => { ActivePane = 1; episodeList.SetFocus(); };
    feedList.SelectedItemChanged += _ => UpdateEpisodeList();
    feedsFrame.Add(feedList);

    // Rechts: Episoden + Details als Tabs
    // Right pane ohne Rahmen/Titel – Tabs kümmern sich um Labels
    epsFrame = new View { X = Pos.Right(feedsFrame), Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    mainWin.Add(epsFrame);

    BuildRightTabs();   // <- neu

    // Player unten
    statusFrame = new FrameView("Player") { X = 0, Y = Pos.Bottom(mainWin), Width = Dim.Fill(), Height = 4, CanFocus=false };
    top.Add(statusFrame);
    BuildPlayerUI();

    ApplyTheme(useMenuAccent: true);

    top.KeyPress         += (KeyArgs e) => { if (HandleGlobalKeys(e)) e.Handled = true; };
    feedList.KeyPress    += (KeyArgs e) => { if (HandleGlobalKeys(e)) e.Handled = true; };
    episodeList.KeyPress += (KeyArgs e) => { if (HandleGlobalKeys(e)) e.Handled = true; };

    feedList.SetFocus();
    UpdateEpisodeList();      // lädt auch Details

    _progressTimerToken = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ => {
        if (Player != null) UpdatePlayerUI(Player.State);
        return true;
    });

    try { Application.Run(); }
    catch (Exception ex) { Log.Fatal(ex, "Application.Run crashed"); throw; }
    finally {
        try { Player?.Stop(); } catch { }
        try { (Player as IDisposable)?.Dispose(); } catch { }
        try { await AppStorage.SaveAsync(Data); } catch { }

        try { Application.Shutdown(); } catch { }
        ResetTerminal();         // <— wichtig
        Log.Information("Shutdown complete");
        try { Log.CloseAndFlush(); } catch { }
    }

}

    static TabView rightTabs = null!;
    static TextView detailsView = null!;

    static void BuildRightTabs() {
        epsFrame.RemoveAll();

        rightTabs = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        epsFrame.Add(rightTabs);

        // Tab "Episodes"
        var epHost = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        episodeList = new ListView(new System.Collections.Generic.List<string>()) {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()
        };
        episodeList.OpenSelectedItem += _ => PlaySelectedEpisode();
        episodeList.SelectedItemChanged += _ => UpdateDetailsPane(); // Details live updaten
        epHost.Add(episodeList);

        // Tab "Details"
        var detFrame = new FrameView("Shownotes") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        detailsView = new TextView {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = true
        };
        detFrame.Add(detailsView);

        rightTabs.AddTab(new TabView.Tab("Episodes", epHost), true);
        rightTabs.AddTab(new TabView.Tab("Details",  detFrame), false);
    }


    // ---------- Player UI ----------
    static void BuildPlayerUI() {
        statusFrame.RemoveAll();

        // Zeile 0: Titel links (gekürzt), Zeitblock rechts
        titleLabel = new Label("—") { X = 2, Y = 0, Width = Dim.Fill(34), Height = 1 };
        timeLabel  = new Label("⏸ 00:00 / --:--  (-:--)  Vol 0%  1.0×") {
            X = Pos.AnchorEnd(32), Y = 0, Width = 32, Height = 1, TextAlignment = TextAlignment.Right
        };

        // Zeile 1: kompakte Controls
        btnBack10    = new Button("«10s")      { X = 2, Y = 1 };
        btnPlayPause = new Button("Play ⏵")    { X = Pos.Right(btnBack10) + 1, Y = 1 };
        btnFwd10     = new Button("10s»")      { X = Pos.Right(btnPlayPause) + 1, Y = 1 };
        btnVolDown   = new Button("Vol−")      { X = Pos.Right(btnFwd10) + 3, Y = 1 };
        btnVolUp     = new Button("Vol+")      { X = Pos.Right(btnVolDown) + 1, Y = 1 };
        btnDownload  = new Button("⬇ Download"){ X = Pos.Right(btnVolUp) + 3, Y = 1 };

        // Zeile 2: Progressbar
        progress = new ProgressBar() { X = 2, Y = 2, Width = Dim.Fill(2), Height = 1 };

        // Click-Handler
        btnBack10.Clicked    += () => Player?.SeekRelative(TimeSpan.FromSeconds(-10));
        btnPlayPause.Clicked += () => { Player?.TogglePause(); UpdatePlayPauseButton(); };
        btnFwd10.Clicked     += () => Player?.SeekRelative(TimeSpan.FromSeconds(+10));
        btnVolDown.Clicked   += () => { if (Player != null) Player.SetVolume(Player.State.Volume0_100 - 5); };
        btnVolUp.Clicked     += () => { if (Player != null) Player.SetVolume(Player.State.Volume0_100 + 5); };
        btnDownload.Clicked  += () => MessageBox.Query("Download", "Downloads kommen später (M5).", "OK");

        statusFrame.Add(titleLabel, timeLabel,
                        btnBack10, btnPlayPause, btnFwd10, btnVolDown, btnVolUp, btnDownload,
                        progress);

        UpdatePlayPauseButton();
    }

    static void UpdatePlayPauseButton() {
        if (btnPlayPause == null || Player == null) return;
        btnPlayPause.Text = Player.State.IsPlaying ? "Pause ⏸" : "Play ⏵";
    }

    static void UpdatePlayerUI(PlayerState s) {
    // --- Anzeige aktualisieren ---
    string F(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    var pos = s.Position;
    var len = s.Length ?? TimeSpan.Zero;

    var posStr = F(pos);
    var lenStr = len == TimeSpan.Zero ? "--:--" : F(len);
    var remStr = len == TimeSpan.Zero ? "--:--" : F((len - pos) < TimeSpan.Zero ? TimeSpan.Zero : (len - pos));
    var icon   = s.IsPlaying ? "▶" : "⏸";

    timeLabel.Text = $"{icon} {posStr} / {lenStr}  (-{remStr})  Vol {s.Volume0_100}%  {s.Speed:0.0}×";
    UpdatePlayPauseButton();

    if (len.TotalMilliseconds > 0) {
        progress.Fraction = Math.Clamp((float)(pos.TotalMilliseconds / len.TotalMilliseconds), 0f, 1f);
    } else {
        progress.Fraction = 0f;
    }

    // --- PERSIST-SEKTION: Fortschritt im Modell + Speichern ------------------
    if (currentEpisode != null) {
        try {
            // Fortschritt ins Modell schreiben
            if (len.TotalMilliseconds > 0) currentEpisode.LengthMs = (long)len.TotalMilliseconds;
            currentEpisode.LastPosMs = (long)Math.Max(0, pos.TotalMilliseconds);

            // "played" heuristik
            if (len.TotalMilliseconds > 0) {
                var remain = len - pos;
                var ratio  = pos.TotalMilliseconds / len.TotalMilliseconds;
                if (ratio >= 0.90 || remain <= TimeSpan.FromSeconds(30)) {
                    if (!currentEpisode.Played) {
                        currentEpisode.Played = true;
                        currentEpisode.LastPlayedAt = DateTimeOffset.Now;
                        Log.Information("Episode marked played: {Title}", currentEpisode.Title);
                        SaveSoonDebounced(300); // schnell nach dem Markieren sichern
                    }
                }
            }

            // Liste ca. 1x pro Sekunde auffrischen
            if ((DateTime.UtcNow - _lastUiProgressRefresh) > TimeSpan.FromSeconds(1)) {
                _lastUiProgressRefresh = DateTime.UtcNow;
                UpdateEpisodeList();
            }

            // PERIODISCHES, ZUVERLÄSSIGES SPEICHERN (Throttle ~3s)
            SavePeriodicThrottle(3000);

        } catch (Exception ex) {
            Log.Debug(ex, "UpdatePlayerUI persist error");
        }
    }
    // -------------------------------------------------------------------------
}

    static void OnPlayerStateChanged(PlayerState s) {
        try {
            Application.MainLoop?.Invoke(() => UpdatePlayerUI(s));
        } catch { /* beim Shutdown kann MainLoop schon weg sein – ignorieren */ }
    }

    // ---------- Lists / Feeds ----------
    static void MoveList(int delta) {
        var lv = ActivePane == 0 ? feedList : episodeList;
        if (lv.Source?.Count > 0) {
            lv.SelectedItem = Math.Clamp(lv.SelectedItem + delta, 0, lv.Source.Count - 1);
            if (ActivePane == 0) UpdateEpisodeList();
        }
    }

    static void UpdateEpisodeList() {
        var feed = GetSelectedFeed();
        var items = (feed == null)
            ? new System.Collections.Generic.List<string>()
            : Data.Episodes.Where(e => e.FeedId == feed.Id)
                .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                .Select(EpisodeRowText)
                .ToList();

        // Auswahl beibehalten
        var sel = Math.Clamp(episodeList.SelectedItem, 0, Math.Max(0, items.Count - 1));
        episodeList.SetSource(items);
        episodeList.SelectedItem = sel;
    }

    
    static void UpdateDetailsPane() {
        if (rightTabs == null || detailsView == null) return;
        var ep = GetSelectedEpisode();
        detailsView.Text = ep == null ? "(No episode selected)" : FormatEpisodeDetails(ep);
    }
    
    static void TogglePlayedSelected() {
        var ep = GetSelectedEpisode();
        if (ep == null) return;
        ep.Played = !ep.Played;
        if (ep.Played) {
            ep.LastPlayedAt = DateTimeOffset.Now;
            // wenn als gespielt markiert → Position ans Ende setzen
            if (ep.LengthMs is long len) ep.LastPosMs = len;
        }
        UpdateEpisodeList();
        _ = Task.Run(async () => { try { await AppStorage.SaveAsync(Data); } catch {} });
    }

    static void ResetTerminal()
    {
        try {
            // Maus-Reporting aus (alle Modi), Bracketed Paste aus, Alt-Screen raus, Cursor an, Attribute reset
            Console.Write(
                "\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l" + // mouse off
                "\x1b[?2004l" +                                           // bracketed paste off
                "\x1b[?25h"   +                                           // cursor show
                "\x1b[0m"     +                                           // attributes reset
                "\x1b[?1049l"                                             // leave alt screen
            );
            Console.Out.Flush();
        } catch { /* ignore */ }
    }



    static string FormatEpisodeDetails(Episode e)
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

        // Nur Text – kein DescriptionHtml im Modell
        var notes = e.DescriptionText?.Trim();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes) ? "(no shownotes)" : notes);
        return sb.ToString();
    }

    static bool OnDetails() =>
        rightTabs?.SelectedTab?.Text.ToString() == "Details";

    static TabView.Tab? GetTabByTitle(string title) =>
        rightTabs?.Tabs.FirstOrDefault(t => string.Equals(t.Text.ToString(), title, StringComparison.Ordinal));

    static void FocusDetailsTab()
    {
        if (rightTabs == null) return;
        var tab = GetTabByTitle("Details") ?? rightTabs.Tabs.ElementAtOrDefault(1);
        if (tab != null) { rightTabs.SelectedTab = tab; detailsView?.SetFocus(); }
    }

    static void FocusEpisodesTab()
    {
        if (rightTabs == null) return;
        var tab = GetTabByTitle("Episodes") ?? rightTabs.Tabs.ElementAtOrDefault(0);
        if (tab != null) { rightTabs.SelectedTab = tab; episodeList?.SetFocus(); }
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

        Log.Information("UI PlaySelectedEpisode feed={Feed} ep={Episode}", GetSelectedFeed()?.Title, ep.Title);

        // alte Resume-Versuche brauchen wir nicht mehr
        // (Wenn Du die Methoden noch hast, machen sie nichts; sonst gelöscht.)
        // CancelResumeAttempts();  // <- entfernen

        // sofern vorher etwas lief: einmal sichern
        if (currentEpisode != null && !ReferenceEquals(currentEpisode, ep)) {
            SaveSoonDebounced(200);
        }

        currentEpisode = ep;
        titleLabel.Text = ep.Title ?? "(untitled)";

        // Resume-Entscheidung (nahe Anfang/Ende → neu starten)
        long? startMs = ep.LastPosMs;
        if (startMs is long ms && ep.LengthMs is long len &&
            (ms < 5_000 || ms > (len - 10_000)))
            startMs = null;

        Player.Play(ep.AudioUrl, startMs);

        UpdateDetailsPane();
        SaveSoonDebounced(400); // Auswahl & evtl. neue Timestamps zeitnah sichern
    }

    
    static void SaveSoonDebounced(int delayMs = 1200)
    {
        if (_shuttingDown) return;

        try { _saveDebounceCts?.Cancel(); } catch { }
        _saveDebounceCts = new System.Threading.CancellationTokenSource();
        var ct = _saveDebounceCts.Token;

        _inflightSave = Task.Run(async () =>
        {
            try {
                await Task.Delay(delayMs, ct);
                if (ct.IsCancellationRequested || _shuttingDown) return;

                await _saveGate.WaitAsync(ct);
                try {
                    await AppStorage.SaveAsync(Data);
                    Log.Debug("debounced save ok");
                }
                finally {
                    _saveGate.Release();
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { Log.Debug(ex, "debounced save failed"); }
        }, ct);
    }

    static void SaveNow()
    {
        try { _saveDebounceCts?.Cancel(); } catch { }
        try {
            _saveGate.Wait();
            try { AppStorage.SaveAsync(Data).GetAwaiter().GetResult(); Log.Debug("save now ok"); }
            finally { _saveGate.Release(); }
        } catch (Exception ex) { Log.Debug(ex, "save now failed"); }
    }

    static void SavePeriodicThrottle(int minIntervalMs = 3000)
    {
        if (_shuttingDown) return;
        var now = DateTime.UtcNow;
        if ((now - _lastPeriodicSave).TotalMilliseconds < minIntervalMs) return;
        _lastPeriodicSave = now;

        _ = Task.Run(async () => {
            try {
                await _saveGate.WaitAsync();
                try { await AppStorage.SaveAsync(Data); Log.Debug("periodic save ok"); }
                finally { _saveGate.Release(); }
            } catch (Exception ex) { Log.Debug(ex, "periodic save failed"); }
        });
    }

    
 
  
    // ---------- Global Keys ----------
    static bool HandleGlobalKeys(KeyArgs e) {
    var key = e.KeyEvent.Key;
    var kv  = e.KeyEvent.KeyValue;

    // Blockiere System-Clipboard-Shortcuts (verhindert TextCopy → "Linux/null")
    if ((key & Key.CtrlMask) != 0) {
        var baseKey = key & ~Key.CtrlMask;
        if (baseKey == Key.C || baseKey == Key.V || baseKey == Key.X) { e.Handled = true; return true; }
    }
    
    // Mark as played/unplayed
    if (kv == 'm' || kv == 'M') { TogglePlayedSelected(); return true; }


    // Logs
    if (key == Key.F12) { ShowLogsOverlay(500); return true; }

    // Quit
    if (key == (Key.Q | Key.CtrlMask) || key == Key.Q || kv == 'Q' || kv == 'q') { QuitApp(); return true; }

    // Theme
    if (kv == 't' || kv == 'T') { useMenuAccent = !useMenuAccent; ApplyTheme(useMenuAccent); return true; }

    // ":" und "/"
    if (key == (Key)(':')) { ShowCommandBox(":"); return true; }
    if (key == (Key)('/')) { ShowSearchBox("/"); return true; }

    // Vim-Navigation
    if (key == (Key)('h')) { ActivePane = 0; feedList.SetFocus(); return true; }
    if (key == (Key)('l')) { ActivePane = 1; episodeList.SetFocus(); return true; }
    if (key == (Key)('j')) { MoveList(+1); return true; }
    if (key == (Key)('k')) { MoveList(-1); return true; }

    // Details-Tab togglen
    if (kv == 'i' || kv == 'I') { FocusDetailsTab(); UpdateDetailsPane(); return true; }
    if (key == Key.Esc && OnDetails()) { FocusEpisodesTab(); return true; }

    // Playback
    if (key == Key.Space) { Player?.TogglePause(); UpdatePlayPauseButton(); return true; }
    if (key == Key.CursorLeft || key == (Key)('H')) { Player?.SeekRelative(TimeSpan.FromSeconds(-10)); return true; }
    if (key == Key.CursorRight|| key == (Key)('L')) { Player?.SeekRelative(TimeSpan.FromSeconds(+10)); return true; }
    if (kv == 'H') { Player?.SeekRelative(TimeSpan.FromSeconds(-60)); return true; }
    if (kv == 'L') { Player?.SeekRelative(TimeSpan.FromSeconds(+60)); return true; }

    // g/G
    if (kv == 'g') { Player?.SeekTo(TimeSpan.Zero); return true; }
    if (kv == 'G') { if (Player?.State.Length is TimeSpan len) Player.SeekTo(len); return true; }

    // Volume
    if (key == (Key)('-')) { if (Player != null) Player.SetVolume(Player.State.Volume0_100 - 5); return true; }
    if (key == (Key)('+')) { if (Player != null) Player.SetVolume(Player.State.Volume0_100 + 5); return true; }

    // Speed
    if (key == (Key)('[')) { if (Player != null) Player.SetSpeed(Player.State.Speed - 0.1); return true; }
    if (key == (Key)(']')) { if (Player != null) Player.SetSpeed(Player.State.Speed + 0.1); return true; }
    if (key == (Key)('=')) { if (Player != null) Player.SetSpeed(1.0); return true; }
    if (kv == '1') { Player?.SetSpeed(1.0);  return true; }
    if (kv == '2') { Player?.SetSpeed(1.25); return true; }
    if (kv == '3') { Player?.SetSpeed(1.5);  return true; }

    // Download-Stub
    if (kv == 'd' || kv == 'D') { MessageBox.Query("Download", "Downloads kommen später (M5).", "OK"); return true; }

    // Enter → Play (nicht in Details)
    if (key == Key.Enter && !OnDetails()) { PlaySelectedEpisode(); return true; }

    // Suche wiederholen
    if (key == (Key)('n')) { if (!string.IsNullOrEmpty(lastSearch)) ApplySearch(lastSearch!); return true; }

    return false;
}



    static void ApplyTheme(bool useMenuAccent) {
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

    // ---------- ":" commandline ----------
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
                feedList.SetSource(Data.Feeds.Select(f => f.Title).ToList());
                feedList.SelectedItem = Data.Feeds.FindIndex(f => f.Id == feed.Id);
                UpdateEpisodeList();
            } else if (cmd.StartsWith(":refresh")) {
                await RefreshAllAsync();
            } else if (cmd is ":q" or ":quit") {
                QuitApp();
            } else if (cmd is ":h" or ":help") {
                MessageBox.Query("Keys", KeysHelp, "OK");
            } else if (cmd.StartsWith(":logs")) {
                var arg = cmd.Substring(5).Trim();
                int tail = 500;
                if (int.TryParse(arg, out var n) && n > 0) tail = Math.Min(n, 5000);
                ShowLogsOverlay(tail);
            } else if (cmd.StartsWith(":seek")) {
                var arg = cmd.Substring(5).Trim();
                if (Player == null) return;
                if (string.IsNullOrWhiteSpace(arg)) return;

                if (arg.EndsWith("%") && double.TryParse(arg.TrimEnd('%'), out var pct)) {
                    if (Player.State.Length is TimeSpan len) {
                        var pos = TimeSpan.FromMilliseconds(len.TotalMilliseconds * Math.Clamp(pct/100.0, 0, 1));
                        Player.SeekTo(pos);
                    }
                    return;
                }

                if (arg.StartsWith("+") || arg.StartsWith("-")) {
                    if (int.TryParse(arg, out var secsRel)) {
                        Player.SeekRelative(TimeSpan.FromSeconds(secsRel));
                    }
                    return;
                }

                var parts = arg.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var mm) && int.TryParse(parts[1], out var ss)) {
                    Player.SeekTo(TimeSpan.FromSeconds(mm*60 + ss));
                    return;
                }
            } else if (cmd.StartsWith(":vol")) {
                var arg = cmd.Substring(4).Trim();
                if (Player == null) return;
                if (int.TryParse(arg, out var v)) Player.SetVolume(v);
            } else if (cmd.StartsWith(":speed")) {
                var arg = cmd.Substring(6).Trim();
                if (Player == null) return;
                if (double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp))
                    Player.SetSpeed(sp);
            }
        } catch (Exception ex) {
            MessageBox.ErrorQuery("Command error", ex.Message, "OK");
        }
    }

    // ---------- "/" search ----------
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
  m Mark played/unplayed
  h/l        Focus left/right pane     j/k    Move selection
  Enter      Play selected episode
  /          Search titles+shownotes (Enter to apply, n to repeat)
  :          Command line (e.g. :add URL, :refresh, :q, :h, :logs)
Playback:
  Space      Play/Pause
  ← / →      Seek -10s / +10s   H / L  Seek -60s / +60s
  g / G      Go to start / end
  - / +      Volume down/up
  [ / ]      Slower/Faster    (= reset 1.0×; 1/2/3 = 1.0×/1.25×/1.5×)
Misc:
  F12        Show logs
  d          Download (stub)
  t          Theme toggle (Base/Menu)
  q / Q / Ctrl+Q  Quit
";

    // ---------- Log-Overlay ----------
    static void ShowLogsOverlay(int tail = 500) {
        try {
            var lines = MemLog.Snapshot(tail);
            var dlg = new Dialog($"Logs (last {tail}) — F12/Esc to close", 100, 30);

            var tv = new Terminal.Gui.TextView {
                ReadOnly = true,
                X = 0, Y = 0,
                Width = Dim.Fill(), Height = Dim.Fill(),
                WordWrap = false
            };
            tv.Text = string.Join('\n', lines);
            tv.MoveEnd();

            dlg.KeyPress += (KeyArgs e) => {
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

// ---------- kleiner Memory-Log-Sink für In-App-Viewer ----------
sealed class MemoryLogSink : Serilog.Core.ILogEventSink {
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private readonly int _capacity;
    public MemoryLogSink(int capacity = 2000) => _capacity = Math.Max(100, capacity);

    public void Emit(Serilog.Events.LogEvent logEvent) {
        var ts = logEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        var lvl = logEvent.Level.ToString()[..3].ToUpperInvariant();
        var msg = logEvent.RenderMessage();
        var exc = logEvent.Exception is null ? "" : $"  {logEvent.Exception}";
        var line = $"{ts} [{lvl}] {msg}{exc}";

        _lines.Enqueue(line);
        while (_lines.Count > _capacity && _lines.TryDequeue(out _)) { }
    }

    public string[] Snapshot(int last = 500) {
        var arr = _lines.ToArray();
        if (arr.Length <= last) return arr;
        return arr[^last..];
    }
}
