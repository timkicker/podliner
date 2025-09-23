using System;
using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal sealed class PlayerPanel : FrameView
{
    public Label TitleLabel  { get; private set; } = null!;
    public Label TimeLabel   { get; private set; } = null!;
    public Button BtnPlayPause { get; private set; } = null!;
    public SolidProgressBar Progress { get; private set; } = null!;
    public SolidProgressBar VolBar { get; private set; } = null!;
    public Label VolPctLabel { get; private set; } = null!;
    public Label SpeedLabel  { get; private set; } = null!;

    public Button BtnBack10 { get; private set; } = null!;
    public Button BtnFwd10  { get; private set; } = null!;
    public Button BtnVolDown{ get; private set; } = null!;
    public Button BtnVolUp  { get; private set; } = null!;
    public Button BtnSpeedDown { get; private set; } = null!;
    public Button BtnSpeedUp   { get; private set; } = null!;
    public Button BtnDownload  { get; private set; } = null!;

    public event Action<string>? Command; // ":seek +10", ":toggle", ...

    private const int SidePad = 1;
    private const int PlayerContentH = 5;
    public  const int PlayerFrameH   = PlayerContentH + 2;

    public Func<ColorScheme>? ProgressSchemeProvider { get; set; }

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
        BtnBack10    = new Button("«10s")   { X = 2, Y = 2 };
        BtnPlayPause = new Button("Play ⏵"){ X = Pos.Right(BtnBack10) + gapL, Y = 2 };
        BtnFwd10     = new Button("10s»")   { X = Pos.Right(BtnPlayPause) + gapL, Y = 2 };
        BtnDownload  = new Button("⬇ Download"){ X = Pos.Right(BtnFwd10) + gapL, Y = 2 };

        const int midGap = 2;
        SpeedLabel   = new Label("1.0×") { Width = 6, Y = 0, X = 0, TextAlignment = TextAlignment.Left };
        BtnSpeedDown = new Button("-spd"){ Y = 0, X = Pos.Right(SpeedLabel) + midGap };
        BtnSpeedUp   = new Button("+spd"){ Y = 0, X = Pos.Right(BtnSpeedDown) + midGap };
        var midWidth = 6 + midGap + 6 + midGap + 6;
        var mid = new View { Y = 2, X = Pos.Center(), Width = midWidth, Height = 1, CanFocus = false };
        mid.Add(SpeedLabel, BtnSpeedUp, BtnSpeedDown);

        const int rightPad = 2;
        const int gap = 2;
        int r = rightPad;

        VolBar = new SolidProgressBar { Y = 2, Height = 1, Width = 16, X = Pos.AnchorEnd(r + 16) };
        if (ProgressSchemeProvider != null) VolBar.ColorScheme = ProgressSchemeProvider();
        r += 16 + gap - 2;

        VolPctLabel = new Label("0%") { Y = 2, Width = 4, X = Pos.AnchorEnd(r + 4), TextAlignment = TextAlignment.Left };
        r += 4 + gap + 1;

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

    public void WireSeeks(Action<string> command, Func<TimeSpan> lastEffectiveLength, Action<string> osd)
    {
        Command += command;

        Progress.SeekRequested += frac =>
        {
            var pct = (int)Math.Round(Math.Clamp(frac, 0f, 1f) * 100);
            command($":seek {pct}%");

            var effLen = lastEffectiveLength();
            if (effLen > TimeSpan.Zero)
            {
                var target = TimeSpan.FromMilliseconds(effLen.TotalMilliseconds * frac);
                osd($"→ {(int)target.TotalMinutes:00}:{target.Seconds:00}");
            }
        };

        VolBar.SeekRequested += frac =>
        {
            var vol = (int)Math.Round(Math.Clamp(frac, 0f, 1f) * 100);
            command($":vol {vol}");
            osd($"Vol {vol}%");
        };
    }

    public void Update(PlayerState s, TimeSpan effLen, Func<TimeSpan, string> format)
    {
        var icon   = s.IsPlaying ? "▶" : "⏸";
        var posStr = format(s.Position);
        var lenStr = effLen == TimeSpan.Zero ? "--:--" : format(effLen);
        var rem    = effLen == TimeSpan.Zero ? TimeSpan.Zero : (effLen - s.Position);
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;

        TimeLabel.Text   = $"{icon} {posStr} / {lenStr}  (-{format(rem)})";
        BtnPlayPause.Text = s.IsPlaying ? "Pause ⏸" : "Play ⏵";

        Progress.Fraction = (effLen.TotalMilliseconds > 0)
            ? Math.Clamp((float)(s.Position.TotalMilliseconds / effLen.TotalMilliseconds), 0f, 1f)
            : 0f;

        VolBar.Fraction  = Math.Clamp(s.Volume0_100 / 100f, 0f, 1f);
        VolPctLabel.Text = $"{s.Volume0_100}%";
        SpeedLabel.Text  = $"{s.Speed:0.0}×";
    }
}
