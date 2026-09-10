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
    // Button chrome is "[ " + text + " ]", so this many columns are usable.
    private const int PlayPauseW    = 12;
    private const int PlayPauseTextW = PlayPauseW - 4;

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
    // Deduplicate sub-second render churn. The 250 ms UI timer plus the audio
    // engine's own state-changed bursts (libVLC can fire TimeChanged ~10×/sec)
    // otherwise trigger many renders per second with identical second-precision
    // output — visually perceived as the counter "flying" and elapsed vs
    // remaining briefly disagreeing between renders.
    private int _lastRenderPosSec = -1;
    private int _lastRenderLenSec = -1;
    private int _lastRenderRemSec = -1;
    private bool _lastRenderPlaying = false;
    private int _lastRenderVolume = -1;
    private double _lastRenderSpeed = double.NaN;
    private bool _lastRenderLoading;

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
        BtnPlayPause.Text = Clamp(currentlyPlaying
            ? (isUnicode ? "Play ⏵"  : "Play >")
            : (isUnicode ? "Pause ⏸" : "Pause ||"));

        // toggle icon at start of time label
        var t = TimeLabel.Text?.ToString() ?? "";
        if (t.Length > 0)
        {
            TimeLabel.Text = (currentlyPlaying ? (isUnicode ? "⏸" : "||") : (isUnicode ? "▶" : ">")) +
                             (t.Length > 1 ? t.Substring(1) : "");
        }

        try { SetNeedsDisplay(); Application.Top?.SetNeedsDisplay(); } catch { }
    }


    // Keeps button text inside the fixed width so the row layout holds.
    private static string Clamp(string? text)
    {
        text ??= "";
        return text.Length <= PlayPauseTextW ? text : text[..PlayPauseTextW];
    }

    private void UpdateLoadingVisuals()
    {
        var isUnicode = UIGlyphSet.Current == UIGlyphSet.Profile.Unicode;

        if (_isLoading)
        {
            BtnPlayPause.Text   = Clamp(_loadingText);
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
        // AutoSize would let the button grow with its text and overlap the
        // speed and volume controls to its right; the loading messages are
        // long enough to garble the whole row. Pin the width instead and
        // clamp any text to it.
        BtnPlayPause = new Button("Play " + (UIGlyphSet.Current == UIGlyphSet.Profile.Unicode ? "⏵" : ">"))
            { X = Pos.Right(BtnBack10) + gapL, Y = 2, Width = PlayPauseW, AutoSize = false };
        BtnFwd10     = new Button(UIGlyphSet.Current == UIGlyphSet.Profile.Unicode ? "10s»" : "10>") { X = Pos.Right(BtnPlayPause) + gapL, Y = 2 };
        BtnDownload  = new Button($"{UIGlyphSet.DownloadedMark} Download"){ X = Pos.Right(BtnFwd10) + gapL, Y = 2 };

        const int midGap = 2;
        SpeedLabel   = new Label(UIGlyphSet.SpeedLabel(1.0)) { Width = 6, Y = 0, X = 0, TextAlignment = TextAlignment.Left };
        BtnSpeedDown = new Button("-spd"){ Y = 0, X = Pos.Right(SpeedLabel) + midGap };
        BtnSpeedUp   = new Button("+spd"){ Y = 0, X = Pos.Right(BtnSpeedDown) + midGap };
        // A Terminal.Gui Button renders as "[ text ]", so it is 4 columns
        // wider than its label. The old maths assumed 6 per button and made
        // the container 22 wide, which clipped both speed buttons off the
        // right edge.
        const int speedLabelW = 6;
        const int spdButtonW  = 4 + 4; // "-spd" / "+spd"
        var midWidth = speedLabelW + midGap + spdButtonW + midGap + spdButtonW;
        _mid = new View { Y = 2, X = Pos.Center(), Width = midWidth, Height = 1, CanFocus = false };
        _midWidth = midWidth;
        _mid.Add(SpeedLabel, BtnSpeedDown, BtnSpeedUp); // left -> right
        var mid = _mid;

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

        // Re-evaluate on every layout pass so a terminal resize is picked up.
        LayoutComplete += _ => ApplyResponsiveLayout();
    }

    // ── responsive control row ──────────────────────────────────────────────
    //
    // The row is a left block of transport buttons, a centred speed block and
    // a right block of volume controls. Below roughly 140 columns those three
    // run into each other: at the default 80x25 the skip and download buttons
    // vanished and the volume block painted over the speed buttons.
    //
    // Rather than shrink everything, drop what is least needed at each step.
    // Download duplicates the `d` key, the skip buttons duplicate the arrow
    // keys, and the volume bar is decoration next to the percentage readout.
    private const int WideMinCols   = 140;  // everything fits
    private const int MediumMinCols = 104;  // no download button
    private const int VolBarMinCols = 112;  // volume bar needs its own headroom
    private const int NarrowMinCols = 78;   // no ±spd buttons either

    private View? _mid;
    private int _midWidth;
    private int _lastLayoutWidth = -1;

    private void ApplyResponsiveLayout()
    {
        if (_mid == null || BtnPlayPause == null) return;

        var w = Frame.Width;
        if (w <= 0 || w == _lastLayoutWidth) return;
        _lastLayoutWidth = w;

        var showDownload  = w >= WideMinCols;
        var showSkips     = w >= MediumMinCols;
        // The bar eats 16 columns on the right. Tying it to the same
        // threshold as the skip buttons left the speed and volume blocks
        // touching between 104 and 111 columns.
        var showVolBar    = w >= VolBarMinCols;
        // The `[` and `]` keys cover this, so the buttons are the first thing
        // to go once the row gets really tight.
        var showSpeedBtns = w >= NarrowMinCols;

        BtnDownload.Visible  = showDownload;
        BtnBack10.Visible    = showSkips;
        BtnFwd10.Visible     = showSkips;
        VolBar.Visible       = showVolBar;
        BtnSpeedDown.Visible = showSpeedBtns;
        BtnSpeedUp.Visible   = showSpeedBtns;

        // Positions are assigned numerically rather than through Pos.Right
        // chains, because a hidden view still occupies its old frame and
        // would leave a hole in the row.
        const int pad = 2, gap = 2;
        const int skipW = 8;              // "[ «10s ]"
        const int speedLabelWidth = 6;    // "1.0×"

        int x = pad;
        if (showSkips) { BtnBack10.X = x; x += skipW + gap; }
        BtnPlayPause.X = x; x += PlayPauseW + gap;
        if (showSkips) { BtnFwd10.X = x; x += skipW + gap; }
        if (showDownload) { BtnDownload.X = x; x += 14 + gap; }

        // The volume controls are anchored to the right edge, and those
        // anchors were computed with the bar included. Hiding the bar has to
        // pull them back in, or the block still reserves its 16 columns and
        // runs into the speed block.
        const int volPctW = 5, volBtnW = 6, volBarW = 16;
        int rr = pad;
        if (showVolBar) { VolBar.X = Pos.AnchorEnd(rr + volBarW); rr += volBarW + gap - 2; }
        VolPctLabel.X = Pos.AnchorEnd(rr + volPctW); rr += volPctW + gap + 1;
        BtnVolUp.X    = Pos.AnchorEnd(rr + volBtnW); rr += volBtnW + gap;
        BtnVolDown.X  = Pos.AnchorEnd(rr + volBtnW); rr += volBtnW + gap;

        // Centring the speed block is only safe while there is room on both
        // sides; when narrow, park it directly after the transport buttons.
        // The gap keeps the blocks from touching, which reads as a glitch
        // even when nothing actually overlaps.
        var midW = showSpeedBtns ? _midWidth : speedLabelWidth;
        var rightBlockStart = w - rr;
        var centred = w >= MediumMinCols && x + midW + gap <= rightBlockStart;
        _mid.X = centred ? Pos.Center() : Pos.At(x);
        _mid.Width = midW;
    }

    private bool? _speedEnabledCache;
    public void SetSpeedEnabled(bool enabled)
    {
        // Called on every playback tick (~4×/sec); skip the Terminal.Gui
        // property churn when the state hasn't actually changed.
        if (_speedEnabledCache == enabled) return;
        _speedEnabledCache = enabled;
        BtnSpeedDown.Enabled = enabled;
        BtnSpeedUp.Enabled   = enabled;
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

        // Quantize to whole seconds for display. We derive remSec from the
        // already-floored values so elapsed + remaining always equals length
        // on screen; using an unfloored diff makes pos + rem drift by 1s
        // against len, which is the "not in sync" symptom users notice.
        int posSec = (int)Math.Floor(pos.TotalSeconds);
        int lenSec = (int)Math.Floor(len.TotalSeconds);
        int remSec = Math.Max(0, lenSec - posSec);

        // Loading state must be updated before we early-return: if we skip
        // because nothing changed at second granularity but the engine did
        // start playing (posSec > 0), the loading flag still needs clearing.
        if (_isLoading && (snap.IsPlaying || posSec > 0))
            _isLoading = false;

        int vol = Math.Clamp(volume0to100, 0, 100);

        // Dedup: if the user-visible state hasn't changed at second precision,
        // skip the render. Keeps the display calm and makes elapsed/remaining
        // perfectly in sync (they can only change together).
        if (posSec == _lastRenderPosSec &&
            lenSec == _lastRenderLenSec &&
            remSec == _lastRenderRemSec &&
            snap.IsPlaying == _lastRenderPlaying &&
            vol == _lastRenderVolume &&
            Math.Abs(snap.Speed - _lastRenderSpeed) < 0.001 &&
            _isLoading == _lastRenderLoading)
        {
            return;
        }

        _lastRenderPosSec = posSec;
        _lastRenderLenSec = lenSec;
        _lastRenderRemSec = remSec;
        _lastRenderPlaying = snap.IsPlaying;
        _lastRenderVolume = vol;
        _lastRenderSpeed = snap.Speed;
        _lastRenderLoading = _isLoading;

        pos = TimeSpan.FromSeconds(Math.Clamp(posSec, 0, Math.Max(0, lenSec)));
        len = TimeSpan.FromSeconds(Math.Max(0, lenSec));
        var rem = TimeSpan.FromSeconds(remSec);

        var isUnicode = UIGlyphSet.Current == UIGlyphSet.Profile.Unicode;
        var icon = snap.IsPlaying ? isUnicode ? "▶" : ">" : isUnicode ? "⏸" : "||";

        var posStr = format(pos);
        var lenStr = lenSec == 0 ? "--:--" : format(len);
        var remStr = lenSec == 0 ? "--:--" : format(rem);

        TimeLabel.Text = $"{icon} {posStr} / {lenStr}  (-{remStr})";

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

        VolBar.Fraction  = vol / 100f;
        VolPctLabel.Text = UIGlyphSet.VolumePercent(vol);
        SpeedLabel.Text  = UIGlyphSet.SpeedLabel(snap.Speed);

        UpdateLoadingVisuals();
    }


}
