using System;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

internal sealed class OsdOverlay
{
    private readonly object _gate = new();
    private FrameView? _win;
    private Label? _label;
    private object? _timeout;
    private string _lastText = string.Empty;
    private const int Padding = 2;   // 1 space left + 1 space right
    private const int MinWidth = 10; // keep it readable

    /// <summary>Thread-safe OSD display. Can be called from any thread.</summary>
    public void Show(string text, int ms)
    {
        if (Application.MainLoop != null)
            Application.MainLoop.Invoke(() => ShowOnUi(text, TimeSpan.FromMilliseconds(ms)));
        else
            ShowOnUi(text, TimeSpan.FromMilliseconds(ms));
    }

    public void Show(string text, TimeSpan duration)
    {
        if (Application.MainLoop != null)
            Application.MainLoop.Invoke(() => ShowOnUi(text, duration));
        else
            ShowOnUi(text, duration);
    }

    /// <summary>Thread-safe hide.</summary>
    public void Hide()
    {
        if (Application.MainLoop != null)
            Application.MainLoop.Invoke(HideOnUi);
        else
            HideOnUi();
    }

    public void ApplyTheme()
    {
        if (Application.MainLoop != null)
            Application.MainLoop.Invoke(ApplyThemeOnUi);
        else
            ApplyThemeOnUi();
    }

    // -------------------- UI thread only below --------------------

    private void ShowOnUi(string text, TimeSpan duration)
    {
        EnsureCreated();

        var driverCols = Application.Driver?.Cols ?? 80;
        var maxWidth = Math.Max(MinWidth, driverCols - 4); // 2 cols margin each side
        var desired = Math.Clamp(text.Length + Padding, MinWidth, maxWidth);

        // Clip to one line (no wrapping)
        var clippedText = text;
        if (text.Length + Padding > maxWidth && maxWidth >= Padding)
        {
            var roomForText = Math.Max(0, maxWidth - Padding - 1); // leave space for ellipsis
            clippedText = roomForText > 0 && text.Length > roomForText
                ? text.Substring(0, roomForText) + "…"
                : text;
            desired = Math.Clamp(clippedText.Length + Padding, MinWidth, maxWidth);
        }

        _lastText = clippedText;
        _label!.Text = _lastText;

        _win!.Width = Dim.Sized(desired);
        _win.Height = Dim.Sized(3);
        _win.X = Pos.Center();
        _win.Y = Pos.At(1); // slightly below top edge
        _win.Visible = true;

        // reset timeout
        if (_timeout != null)
        {
            try { Application.MainLoop.RemoveTimeout(_timeout); } catch { }
            _timeout = null;
        }

        _timeout = Application.MainLoop.AddTimeout(duration, _ =>
        {
            HideOnUi();
            return false;
        });

        Application.Top?.SetNeedsDisplay();
    }

    private void HideOnUi()
    {
        if (_win == null) return;

        if (_timeout != null)
        {
            try { Application.MainLoop.RemoveTimeout(_timeout); } catch { }
            _timeout = null;
        }

        _win.Visible = false;
        Application.Top?.SetNeedsDisplay();
    }

    private void ApplyThemeOnUi()
    {
        var scheme = Application.Top?.ColorScheme ?? Colors.Base;
        if (_win   != null) _win.ColorScheme   = scheme;
        if (_label != null) _label.ColorScheme = scheme;
        Application.Top?.SetNeedsDisplay();
    }



    private void EnsureCreated()
    {
        if (_win != null) return;

        _label = new Label(string.Empty)
        {
            X = Pos.Center(),
            Y = Pos.Center()
        };

        _win = new FrameView("")
        {
            Width = Dim.Sized(MinWidth),
            Height = Dim.Sized(3),
            CanFocus = false,
            X = Pos.Center(),
            Y = Pos.At(1),
            ColorScheme = Application.Top?.ColorScheme ?? Colors.Base
        };


        _win.Border.BorderStyle = BorderStyle.Rounded;
        _win.Add(_label);
        _win.Visible = false;

        Application.Top.Add(_win);

        // Re-layout on terminal resize
        Application.Top.Resized += _ =>
        {
            if (_win?.Visible == true && !string.IsNullOrEmpty(_lastText))
            {
                ShowOnUi(_lastText, TimeSpan.FromMilliseconds(10));
            }
        };
    }
}
