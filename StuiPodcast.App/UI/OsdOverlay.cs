using System;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

internal sealed class OsdOverlay
{
    private FrameView? _win;
    private Label? _label;
    private object? _timeout;

    public void Show(string text, int ms)
    {
        EnsureCreated();

        _label!.Text = text;
        _win!.Visible = true;
        Application.Top?.SetNeedsDisplay();

        if (_timeout != null)
            try { Application.MainLoop.RemoveTimeout(_timeout); } catch { }

        _timeout = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(ms), _ =>
        {
            if (_win != null) _win.Visible = false;
            Application.Top?.SetNeedsDisplay();
            return false;
        });
    }

    public void ApplyTheme()
    {
        if (_win != null) _win.ColorScheme = Colors.Menu;
    }

    private void EnsureCreated()
    {
        if (_win != null) return;

        _label = new Label("") { X = Pos.Center(), Y = Pos.Center() };
        _win = new FrameView("")
        {
            Width = 24, Height = 3, CanFocus = false,
            X = Pos.Center(), Y = Pos.Center(),
            ColorScheme = Colors.Menu
        };
        _win.Border.BorderStyle = BorderStyle.Rounded;
        _win.Add(_label);
        _win.Visible = false;
        Application.Top.Add(_win);
    }
}