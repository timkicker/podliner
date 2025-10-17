using System;
using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal sealed class PlayerPanel : FrameView
{
    public Label TitleLabel    { get; private set; } = null!;
    public Label TimeLabel     { get; private set; } = null!;
    public Button BtnPlayPause { get; private set; } = null!;
    public SolidProgressBar Progress { get; private set; } = null!;
    public SolidProgressBar VolBar   { get; private set; } = null!;
    public Label VolPctLabel   { get; private set; } = null!;
    public Label SpeedLabel    { get; private set; } = null!;

    public Button BtnBack10    { get; private set; } = null!;
    public Button BtnFwd10     { get; private set; } = null!;
    public Button BtnVolDown   { get; private set; } = null!;
    public Button BtnVolUp     { get; private set; } = null!;
    public Button BtnSpeedDown { get; private set; } = null!;
    public Button BtnSpeedUp   { get; private set; } = null!;
    public Button BtnDownload  { get; private set; } = null!;

    /// <summary>Router-Hook (z. B. ":seek +10")</summary>
    public event Action<string>? Command;

    private const int SidePad = 1;
    private const int PlayerContentH = 5;
    public  const int PlayerFrameH   = PlayerContentH + 2;

    public Func<ColorScheme>? ProgressSchemeProvider { get; set; }

    // wiring guards / throttles
    private bool _commandAttached;
    private bool _seeksWired;

    private DateTime _lastProgressEmit = DateTime.MinValue;
    private float _lastProgressFrac = -1f;
    private DateTime _lastVolEmit = DateTime.MinValue;
    private float _lastVolFrac = -1f;

    private const int   DragThrottleMs   = 90;     // min interval between emits while dragging
    private const float ProgressDeltaMin = 0.01f;  // min fraction change (~1%) to emit
    private const float VolumeDeltaMin   = 0.02f;  // 2% volume step to emit
    
    // PlayerPanel.cs (oben in der Klasse)
    private bool _isLoading;
    private string _loadingText = "Loading…";
    private TimeSpan _loadingBaseline = TimeSpan.Zero;
    private DateTime _loadingSinceUtc = DateTime.MinValue;

// Tuning
    private static readonly TimeSpan LoadingAdvanceThreshold = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LoadingMinVisible       = TimeSpan.FromMilliseconds(300);

// optional: letzte bekannte Pos für Baseline-Fallback
    private TimeSpan _lastPosSnapshot = TimeSpan.Zero;


// öffentlich:
    // PlayerPanel.cs – öffentliches API (unter Build()):
    public void SetLoading(bool on, string? text = null, TimeSpan? baseline = null)
    {
        _isLoading = on;
        if (!string.IsNullOrWhiteSpace(text))
            _loadingText = text!;
        if (on)
        {
            _loadingSinceUtc = DateTime.UtcNow;
            _loadingBaseline = baseline ?? _lastPosSnapshot; // Startpunkt merken
        }
        UpdateLoadingVisuals();
    }

    private void ClearLoadingIfAdvanced(TimeSpan currentPos)
    {
        if (!_isLoading) return;

        var advanced = (currentPos > _loadingBaseline + LoadingAdvanceThreshold);
        var minTime  = (DateTime.UtcNow - _loadingSinceUtc) >= LoadingMinVisible;

        if (advanced && minTime)
        {
            _isLoading = false;
            UpdateLoadingVisuals();
        }
    }

    private void UpdateLoadingVisuals()
    {
        var isUnicode = GlyphSet.Current == GlyphSet.Profile.Unicode;

        if (_isLoading)
        {
            BtnPlayPause.Text   = _loadingText;
            BtnPlayPause.Enabled = false;

            var t = TimeLabel.Text?.ToString() ?? "";
            if (!t.EndsWith(" ⌛") && !t.EndsWith(" …") && !t.EndsWith(" ⟳"))
                TimeLabel.Text = t + (isUnicode ? " ⌛" : " …");
        }
        else
        {
            BtnPlayPause.Enabled = true; // Text stellt Render() wieder auf Play/Pause um
        }

        // -> sofort neu zeichnen
        try { SetNeedsDisplay(); Application.Top?.SetNeedsDisplay(); } catch { }
    }


    public PlayerPanel() : base("Player")
    {
        X = SidePad;
        Width  = Dim.Fill(SidePad * 2);
        Height = PlayerFrameH;
        CanFocus = false;
        Build();
    }

    private void Build()
    {
        RemoveAll();

        TitleLabel = new Label("—") { X = 2, Y = 0, Width = Dim.Fill(34), Height = 1 };

        TimeLabel  = new Label("⏸ 00:00 / --:--  (-:--)")
        {
            X = Pos.AnchorEnd(32), Y = 0, Width = 32, Height = 1, TextAlignment = TextAlignment.Right
        };

        const int gapL = 2;
        BtnBack10    = new Button(GlyphSet.Current == GlyphSet.Profile.Unicode ? "«10s" : "<10") { X = 2, Y = 2 };
        BtnPlayPause = new Button("Play " + (GlyphSet.Current == GlyphSet.Profile.Unicode ? "⏵" : ">")) { X = Pos.Right(BtnBack10) + gapL, Y = 2, Width = 12};
        BtnFwd10     = new Button(GlyphSet.Current == GlyphSet.Profile.Unicode ? "10s»" : "10>") { X = Pos.Right(BtnPlayPause) + gapL, Y = 2 };
        BtnDownload  = new Button($"{GlyphSet.DownloadedMark} Download"){ X = Pos.Right(BtnFwd10) + gapL, Y = 2 };

        const int midGap = 2;
        SpeedLabel   = new Label(GlyphSet.SpeedLabel(1.0)) { Width = 6, Y = 0, X = 0, TextAlignment = TextAlignment.Left };
        BtnSpeedDown = new Button("-spd"){ Y = 0, X = Pos.Right(SpeedLabel) + midGap };
        BtnSpeedUp   = new Button("+spd"){ Y = 0, X = Pos.Right(BtnSpeedDown) + midGap };
        var midWidth = 6 + midGap + 6 + midGap + 6;
        var mid = new View { Y = 2, X = Pos.Center(), Width = midWidth, Height = 1, CanFocus = false };
        mid.Add(SpeedLabel, BtnSpeedDown, BtnSpeedUp); // links -> rechts

        const int rightPad = 2;
        const int gap = 2;
        int r = rightPad;

        VolBar = new SolidProgressBar { Y = 2, Height = 1, Width = 16, X = Pos.AnchorEnd(r + 16) };
        if (ProgressSchemeProvider != null) VolBar.ColorScheme = ProgressSchemeProvider();
        r += 16 + gap - 2;

        VolPctLabel = new Label(GlyphSet.VolumePercent(0)) { Y = 2, Width = 5, X = Pos.AnchorEnd(r + 5), TextAlignment = TextAlignment.Left };
        r += 5 + gap + 1;

        BtnVolUp   = new Button("Vol+") { Y = 2, X = Pos.AnchorEnd(r + 6) };
        r += 6 + gap;
        BtnVolDown = new Button("Vol−") { Y = 2, X = Pos.AnchorEnd(r + 6) };
        r += 6 + gap;

        Progress = new SolidProgressBar { X = 2, Y = 4, Width = Dim.Fill(2), Height = 1 };
        if (ProgressSchemeProvider != null) Progress.ColorScheme = ProgressSchemeProvider();

        // Clicks → Command
        BtnBack10.Clicked    += () => Command?.Invoke(":seek -10");
        BtnFwd10.Clicked     += () => Command?.Invoke(":seek +10");
        BtnPlayPause.Clicked += () => Command?.Invoke(":toggle");
        BtnVolDown.Clicked   += () => Command?.Invoke(":vol -5");
        BtnVolUp.Clicked     += () => Command?.Invoke(":vol +5");
        BtnSpeedDown.Clicked += () => Command?.Invoke(":speed -0.1");
        BtnSpeedUp.Clicked   += () => Command?.Invoke(":speed +0.1");
        BtnDownload.Clicked  += () => Command?.Invoke(":dl toggle");

        Add(TitleLabel, TimeLabel,
            BtnBack10, BtnPlayPause, BtnFwd10, BtnDownload,
            mid, BtnVolDown, BtnVolUp, VolPctLabel, VolBar, Progress);
    }

    /// <summary>
    /// Verdrahtet Seeks/Volume exakt einmal. Mehrfachaufrufe sind idempotent.
    /// </summary>
    public void WireSeeks(Action<string> command, Func<TimeSpan> lastEffectiveLength, Action<string> osd)
    {
        if (!_commandAttached)
        {
            Command += command;
            _commandAttached = true;
        }
        if (_seeksWired) return;
        _seeksWired = true;

        Progress.SeekRequested += frac =>
        {
            var now = DateTime.UtcNow;
            var clamped = Math.Clamp(frac, 0f, 1f);

            if ((now - _lastProgressEmit).TotalMilliseconds < DragThrottleMs &&
                Math.Abs(clamped - _lastProgressFrac) < ProgressDeltaMin)
                return;

            _lastProgressEmit = now;
            _lastProgressFrac = clamped;

            var pct = (int)Math.Round(clamped * 100);
            command($":seek {pct}%");

            var effLen = lastEffectiveLength();
            if (effLen > TimeSpan.Zero)
            {
                var target = TimeSpan.FromMilliseconds(effLen.TotalMilliseconds * clamped);
                var h = (int)target.TotalHours;
                var mm = target.Minutes;
                var ss = target.Seconds;
                var txt = h > 0 ? $"{h}:{mm:00}:{ss:00}" : $"{mm:00}:{ss:00}";
                osd($"→ {txt}");
            }
        };

        VolBar.SeekRequested += frac =>
        {
            var now = DateTime.UtcNow;
            var clamped = Math.Clamp(frac, 0f, 1f);

            if ((now - _lastVolEmit).TotalMilliseconds < DragThrottleMs &&
                Math.Abs(clamped - _lastVolFrac) < VolumeDeltaMin)
                return;

            _lastVolEmit = now;
            _lastVolFrac = clamped;

            var vol = (int)Math.Round(clamped * 100);
            var v = Math.Clamp(vol, 0, 100);
            command($":vol {v}");
            VolBar.Fraction  = v / 100f; // visuelles Feedback sofort
            VolPctLabel.Text = GlyphSet.VolumePercent(v);
        };
    }

    // ----------------------------------------------------------------------
    // Bevorzugtes Update: EIN atomischer Snapshot = synchrones UI
    // ----------------------------------------------------------------------
    public void Update(PlaybackSnapshot snap, int volume0to100, Func<TimeSpan, string> format)
        => RenderFromSnapshot(snap, volume0to100, format);

    // ----------------------------------------------------------------------
    // ALT: Abwärtskompatibles Update — delegiert intern auf Snapshot
    // ----------------------------------------------------------------------
    public void Update(PlayerState s, TimeSpan effLen, Func<TimeSpan, string> format)
    {
        var snap = new PlaybackSnapshot(
            0,                      // SessionId
            null,                   // EpisodeId
            s.Position < TimeSpan.Zero ? TimeSpan.Zero : s.Position,
            effLen   < TimeSpan.Zero ? TimeSpan.Zero : effLen,
            s.IsPlaying,
            s.Speed <= 0 ? 1.0 : s.Speed,
            DateTimeOffset.Now
        );
        RenderFromSnapshot(snap, s.Volume0_100, format);
    }

    // ----------------------------------------------------------------------
    // Gemeinsame Render-Logik (eine Quelle → synchrones UI)
    // ----------------------------------------------------------------------
    private void RenderFromSnapshot(PlaybackSnapshot snap, int volume0to100, Func<TimeSpan, string> format)
    {
        var pos = snap.Position < TimeSpan.Zero ? TimeSpan.Zero : snap.Position;
        _lastPosSnapshot = pos;
        ClearLoadingIfAdvanced(pos);
        var len = snap.Length   < TimeSpan.Zero ? TimeSpan.Zero : snap.Length;
        if (pos > len && len > TimeSpan.Zero) pos = len;

        var rem = len == TimeSpan.Zero ? TimeSpan.Zero : (len - pos);
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;

        var isUnicode = GlyphSet.Current == GlyphSet.Profile.Unicode;
        var icon = snap.IsPlaying ? (isUnicode ? "▶" : ">") : (isUnicode ? "⏸" : "||");

        var posStr = format(pos);
        var lenStr = len == TimeSpan.Zero ? "--:--" : format(len);

        TimeLabel.Text    = $"{icon} {posStr} / {lenStr}  (-{format(rem)})";

        // <<< NEW: Loading-Logik automatisch zurücknehmen, sobald echte Wiedergabe anläuft
        if (_isLoading && (snap.IsPlaying || pos > TimeSpan.Zero))
            _isLoading = false;

        // Button-Text hängt von Loading / Play/Pause ab
        if (_isLoading)
            BtnPlayPause.Text = _loadingText;
        else
            BtnPlayPause.Text = snap.IsPlaying
                ? (isUnicode ? "Pause ⏸" : "Pause ||")
                : (isUnicode ? "Play ⏵"  : "Play >");

        Progress.Fraction = (len.TotalMilliseconds > 0)
            ? Math.Clamp((float)(pos.TotalMilliseconds / len.TotalMilliseconds), 0f, 1f)
            : 0f;

        var v = Math.Clamp(volume0to100, 0, 100);
        VolBar.Fraction  = v / 100f;
        VolPctLabel.Text = GlyphSet.VolumePercent(v);
        SpeedLabel.Text  = GlyphSet.SpeedLabel(snap.Speed);

        // Button Enabled/Suffix sauber halten
        UpdateLoadingVisuals();
    }

}
