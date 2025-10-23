using System;
using Terminal.Gui;

namespace StuiPodcast.App.UI.Controls;

/// <summary>Eine solide Progressbar (Track=BG, Fill=Accent-FG) mit Mouse-Seek.</summary>
internal sealed class UiSolidProgressBar : View
{
    private DateTime _lastEmit = DateTime.MinValue;
    private float _fraction;
    public float Fraction
    {
        get => _fraction;
        set { _fraction = Math.Clamp(value, 0f, 1f); SetNeedsDisplay(); }
    }

    public event Action<float>? SeekRequested;

    public UiSolidProgressBar()
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
        bool isDownLike =
            me.Flags.HasFlag(MouseFlags.Button1Clicked) ||
            me.Flags.HasFlag(MouseFlags.Button1Pressed) ||
            me.Flags.HasFlag(MouseFlags.Button1DoubleClicked) ||
            me.Flags.HasFlag(MouseFlags.Button1Released);

        // Optional: kontinuierliches Draggen nur leicht drosseln
        bool isDrag = me.Flags.HasFlag(MouseFlags.ReportMousePosition) && me.Flags.HasFlag(MouseFlags.Button1Pressed);
        if (!(isDownLike || isDrag)) return base.MouseEvent(me);

        // Drossel: max. alle 90 ms bei Drag
        if (isDrag && DateTime.UtcNow - _lastEmit < TimeSpan.FromMilliseconds(90))
            return true;

        int width = Math.Max(1, Bounds.Width - 1);
        int localX = Math.Clamp(me.X, 0, Math.Max(0, Bounds.Width - 1));
        float frac = width <= 0 ? 0f : (float)localX / width;

        _lastEmit = DateTime.UtcNow;
        SeekRequested?.Invoke(frac);
        return true;
    }
}