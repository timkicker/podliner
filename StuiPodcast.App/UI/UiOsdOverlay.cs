using System;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

internal sealed class UiOsdOverlay
{
    #region fields
    private readonly object _gate = new();
    private FrameView? _win;
    private Label? _label;
    private object? _timeout;
    private string _lastText = string.Empty;
    private Toplevel? _root;          // captured at Build() time; stable for app lifetime
    private const int Padding = 2;   // 1 space left + 1 space right
    private const int MinWidth = 10; // keep it readable
    #endregion

    #region public api (thread-safe)
    // Call once from UiShell.Build() while Application.Top is the stable root Toplevel.
    // Eagerly parents _win so it is never orphaned into a dialog's subview tree.
    public void Initialize(Toplevel root)
    {
        _root = root;
        EnsureCreated();
    }

    // thread-safe osd display
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

    // thread-safe hide
    public void Hide()
    {
        if (Application.MainLoop != null)
            Application.MainLoop.Invoke(HideOnUi);
        else
            HideOnUi();
    }

    // thread-safe theme apply
    public void ApplyTheme()
    {
        if (Application.MainLoop != null)
            Application.MainLoop.Invoke(ApplyThemeOnUi);
        else
            ApplyThemeOnUi();
    }
    #endregion

    #region ui thread only
    private void ShowOnUi(string text, TimeSpan duration)
    {
        EnsureCreated();

        var driverCols = Application.Driver?.Cols ?? 80;
        var maxWidth = Math.Max(MinWidth, driverCols - 4); // 2 cols margin each side
        var desired = Math.Clamp(text.Length + Padding, MinWidth, maxWidth);

        // clip to one line (no wrapping)
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

        _root?.SetNeedsDisplay();
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
        _root?.SetNeedsDisplay();
    }

    private void ApplyThemeOnUi()
    {
        var scheme = _root?.ColorScheme ?? Colors.Base;
        if (_win   != null) _win.ColorScheme   = scheme;
        if (_label != null) _label.ColorScheme = scheme;
        _root?.SetNeedsDisplay();
    }
    #endregion

    #region creation
    private void EnsureCreated()
    {
        if (_win != null) return;

        var root = _root ?? Application.Top;  // fallback if called before Initialize()
        if (root == null) return;

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
            ColorScheme = root.ColorScheme ?? Colors.Base
        };

        _win.Border.BorderStyle = BorderStyle.Rounded;
        _win.Add(_label);
        _win.Visible = false;

        root.Add(_win);

        // relayout on terminal resize
        root.Resized += _ =>
        {
            if (_win?.Visible == true && !string.IsNullOrEmpty(_lastText))
            {
                ShowOnUi(_lastText, TimeSpan.FromMilliseconds(10));
            }
        };
    }
    #endregion
}
