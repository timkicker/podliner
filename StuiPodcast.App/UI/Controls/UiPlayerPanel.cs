using System;
using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI.Controls;

internal sealed class UiPlayerPanel : FrameView
{
    public Label TitleLabel    { get; private set; } = null!;
    public Label TimeLabel     { get; private set; } = null!;
    public Button BtnPlayPause { get; private set; } = null!;
    public UiSolidProgressBar Progress { get; private set; } = null!;
    public UiSolidProgressBar VolBar   { get; private set; } = null!;
    public Label VolPctLabel   { get; private set; } = null!;
    public Label SpeedLabel    { get; private set; } = null!;

    public Button BtnBack10    { get; private set; } = null!;
    public Button BtnFwd10     { get; private set; } = null!;
    public Button BtnVolDown   { get; private set; } = null!;
    public Button BtnVolUp     { get; private set; } = null!;
    public Button BtnSpeedDown { get; private set; } = null!;
    public Button BtnSpeedUp   { get; private set; } = null!;
    public Button BtnDownload  { get; private set; } = null!;

    // router hook (e.g. ":seek +10")
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
    
    // loading control
    private bool _isLoading;
    private string _loadingText = "Loading…";
    private TimeSpan _loadingBaseline = TimeSpan.Zero;
    private DateTime _loadingSinceUtc = DateTime.MinValue;

    // current playing state for optimistic toggle
    private bool _lastKnownPlaying = false;

    // tuning
    private static readonly TimeSpan LoadingAdvanceThreshold = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LoadingMinVisible       = TimeSpan.FromMilliseconds(300);

    // optional: last known pos for baseline fallback
    private TimeSpan _lastPosSnapshot = TimeSpan.Zero;

    // public api for shell/coordinator
    public void SetLoading(bool on, string? text = null, TimeSpan? baseline = null)
    {
        _isLoading = on;
        if (!string.IsNullOrWhiteSpace(text))
            _loadingText = text!;
        if (on)
        {
            _loadingSinceUtc = DateTime.UtcNow;
            _loadingBaseline = baseline ?? _lastPosSnapshot; // record baseline
        }
        UpdateLoadingVisuals();
    }

    private void ClearLoadingIfAdvanced(TimeSpan currentPos)
    {
        if (!_isLoading) return;

        var advanced = currentPos > _loadingBaseline + LoadingAdvanceThreshold;
        var minTime  = DateTime.UtcNow - _loadingSinceUtc >= LoadingMinVisible;

        if (advanced && minTime)
        {
            _isLoading = false;
            UpdateLoadingVisuals();
        }
    }
    
    public void OptimisticToggle()
    {
        var isUnicode = UIGlyphSet.Current == UIGlyphSet.Profile.Unicode;

        // toggle based on last known state
        var currentlyPlaying = _lastKnownPlaying;
        BtnPlayPause.Text = currentlyPlaying
            ? (isUnicode ? "Play ⏵"  : "Play >")
            : (isUnicode ? "Pause ⏸" : "Pause ||");

        // toggle icon at start of time label
        var t = TimeLabel.Text?.ToString() ?? "";
        if (t.Length > 0)
        {
            TimeLabel.Text = (currentlyPlaying ? (isUnicode ? "⏸" : "||") : (isUnicode ? "▶" : ">")) +
                             (t.Length > 1 ? t.Substring(1) : "");
        }

        try { SetNeedsDisplay(); Application.Top?.SetNeedsDisplay(); } catch { }
    }


    private void UpdateLoadingVisuals()
    {
        var isUnicode = UIGlyphSet.Current == UIGlyphSet.Profile.Unicode;

        if (_isLoading)
        {
            BtnPlayPause.Text   = _loadingText;
            BtnPlayPause.Enabled = false;

            var t = TimeLabel.Text?.ToString() ?? "";
            if (!t.EndsWith(" ⧖") && !t.EndsWith(" …") && !t.EndsWith(" ⟳"))
                TimeLabel.Text = t + (isUnicode ? " ⧖" : " …");
        }
        else
        {
            BtnPlayPause.Enabled = true; // text will be set back by render()
        }

        // force redraw
        try { SetNeedsDisplay(); Application.Top?.SetNeedsDisplay(); } catch { }
    }

    public UiPlayerPanel() : base("AudioPlayer")
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
        BtnBack10    = new Button(UIGlyphSet.Current == UIGlyphSet.Profile.Unicode ? "«10s" : "<10") { X = 2, Y = 2 };
        BtnPlayPause = new Button("Play " + (UIGlyphSet.Current == UIGlyphSet.Profile.Unicode ? "⏵" : ">")) { X = Pos.Right(BtnBack10) + gapL, Y = 2, Width = 12};
        BtnFwd10     = new Button(UIGlyphSet.Current == UIGlyphSet.Profile.Unicode ? "10s»" : "10>") { X = Pos.Right(BtnPlayPause) + gapL, Y = 2 };
        BtnDownload  = new Button($"{UIGlyphSet.DownloadedMark} Download"){ X = Pos.Right(BtnFwd10) + gapL, Y = 2 };

        const int midGap = 2;
        SpeedLabel   = new Label(UIGlyphSet.SpeedLabel(1.0)) { Width = 6, Y = 0, X = 0, TextAlignment = TextAlignment.Left };
        BtnSpeedDown = new Button("-spd"){ Y = 0, X = Pos.Right(SpeedLabel) + midGap };
        BtnSpeedUp   = new Button("+spd"){ Y = 0, X = Pos.Right(BtnSpeedDown) + midGap };
        var midWidth = 6 + midGap + 6 + midGap + 6;
        var mid = new View { Y = 2, X = Pos.Center(), Width = midWidth, Height = 1, CanFocus = false };
        mid.Add(SpeedLabel, BtnSpeedDown, BtnSpeedUp); // left -> right

        const int rightPad = 2;
        const int gap = 2;
        int r = rightPad;

        VolBar = new UiSolidProgressBar { Y = 2, Height = 1, Width = 16, X = Pos.AnchorEnd(r + 16) };
        if (ProgressSchemeProvider != null) VolBar.ColorScheme = ProgressSchemeProvider();
        r += 16 + gap - 2;

        VolPctLabel = new Label(UIGlyphSet.VolumePercent(0)) { Y = 2, Width = 5, X = Pos.AnchorEnd(r + 5), TextAlignment = TextAlignment.Left };
        r += 5 + gap + 1;

        BtnVolUp   = new Button("Vol+") { Y = 2, X = Pos.AnchorEnd(r + 6) };
        r += 6 + gap;
        BtnVolDown = new Button("Vol−") { Y = 2, X = Pos.AnchorEnd(r + 6) };
        r += 6 + gap;

        Progress = new UiSolidProgressBar { X = 2, Y = 4, Width = Dim.Fill(2), Height = 1 };
        if (ProgressSchemeProvider != null) Progress.ColorScheme = ProgressSchemeProvider();

        // clicks -> command
        BtnBack10.Clicked    += () => Command?.Invoke(":seek -10");
        BtnFwd10.Clicked     += () => Command?.Invoke(":seek +10");
        BtnPlayPause.Clicked += () =>
        {
            OptimisticToggle();            // immediate feedback
            Command?.Invoke(":toggle");    // actual toggle
        };

        BtnVolDown.Clicked   += () => Command?.Invoke(":vol -5");
        BtnVolUp.Clicked     += () => Command?.Invoke(":vol +5");
        BtnSpeedDown.Clicked += () => Command?.Invoke(":speed -0.1");
        BtnSpeedUp.Clicked   += () => Command?.Invoke(":speed +0.1");
        BtnDownload.Clicked  += () => Command?.Invoke(":dl toggle");

        Add(TitleLabel, TimeLabel,
            BtnBack10, BtnPlayPause, BtnFwd10, BtnDownload,
            mid, BtnVolDown, BtnVolUp, VolPctLabel, VolBar, Progress);
    }

    // wire seeks/volume once; idempotent on repeated calls
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
            VolBar.Fraction  = v / 100f; // immediate visual feedback
            VolPctLabel.Text = UIGlyphSet.VolumePercent(v);
        };
    }

    // preferred update: atomic snapshot -> sync ui
    public void Update(PlaybackSnapshot snap, int volume0to100, Func<TimeSpan, string> format)
        => RenderFromSnapshot(snap, volume0to100, format);

    // legacy update: delegate to snapshot for compatibility
    public void Update(PlayerState s, TimeSpan effLen, Func<TimeSpan, string> format)
    {
        var snap = new PlaybackSnapshot(
            0,                      // session id
            null,                   // episode id
            s.Position < TimeSpan.Zero ? TimeSpan.Zero : s.Position,
            effLen   < TimeSpan.Zero ? TimeSpan.Zero : effLen,
            s.IsPlaying,
            s.Speed <= 0 ? 1.0 : s.Speed,
            DateTimeOffset.Now
        );
        RenderFromSnapshot(snap, s.Volume0_100, format);
    }

    // shared render logic (single source -> sync ui)
    private void RenderFromSnapshot(PlaybackSnapshot snap, int volume0to100, Func<TimeSpan, string> format)
    {
        var pos = snap.Position < TimeSpan.Zero ? TimeSpan.Zero : snap.Position;
        _lastPosSnapshot = pos;
        ClearLoadingIfAdvanced(pos);

        var len = snap.Length < TimeSpan.Zero ? TimeSpan.Zero : snap.Length;
        if (pos > len && len > TimeSpan.Zero) pos = len;

        // quantize to whole seconds for consistency
        int posSec = (int)Math.Floor(pos.TotalSeconds);             // or Math.Round(...) if preferred
        int lenSec = (int)Math.Floor(len.TotalSeconds);
        pos = TimeSpan.FromSeconds(Math.Clamp(posSec, 0, Math.Max(0, lenSec)));
        len = TimeSpan.FromSeconds(Math.Max(0, lenSec));

        var remSec = Math.Max(0, lenSec - posSec);
        var rem = TimeSpan.FromSeconds(remSec);

        var isUnicode = UIGlyphSet.Current == UIGlyphSet.Profile.Unicode;
        var icon = snap.IsPlaying ? isUnicode ? "▶" : ">" : isUnicode ? "⏸" : "||";

        var posStr = format(pos);
        var lenStr = lenSec == 0 ? "--:--" : format(len);
        var remStr = lenSec == 0 ? "--:--" : format(rem);

        TimeLabel.Text = $"{icon} {posStr} / {lenStr}  (-{remStr})";

        if (_isLoading && (snap.IsPlaying || posSec > 0))
            _isLoading = false;

        if (_isLoading)
            BtnPlayPause.Text = _loadingText;
        else
            BtnPlayPause.Text = snap.IsPlaying
                ? isUnicode ? "Pause ⏸" : "Pause ||"
                : isUnicode ? "Play ⏵"  : "Play >";

        _lastKnownPlaying = snap.IsPlaying;

        Progress.Fraction = lenSec > 0
            ? Math.Clamp((float)posSec / lenSec, 0f, 1f)
            : 0f;

        var v = Math.Clamp(volume0to100, 0, 100);
        VolBar.Fraction  = v / 100f;
        VolPctLabel.Text = UIGlyphSet.VolumePercent(v);
        SpeedLabel.Text  = UIGlyphSet.SpeedLabel(snap.Speed);

        UpdateLoadingVisuals();
    }


}
