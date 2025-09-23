using System;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

/// <summary>Eine solide Progressbar (Track=BG, Fill=Accent-FG) mit Mouse-Seek.</summary>
internal sealed class SolidProgressBar : View
{
    private float _fraction;
    public float Fraction
    {
        get => _fraction;
        set { _fraction = Math.Clamp(value, 0f, 1f); SetNeedsDisplay(); }
    }

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
        var frac   = width <= 0 ? 0f : (float)localX / (float)width;

        SeekRequested?.Invoke(frac);
        return true;
    }
}