using Terminal.Gui;

namespace StuiPodcast.App.UI;

// Anchor-bottom TextField overlays used by the colon-command prompt (":")
// and the slash-search prompt ("/"). Extracted from UiShell so the widget
// lifecycle (create → focus → remove on Enter/Esc) lives in one place
// instead of being duplicated twice with subtle differences.
internal static class UiShellPrompts
{
    // Returns the created field so the caller can track lifecycle and apply
    // its own colour scheme on theme changes. The `onCommit` callback is
    // invoked with the final text on Enter; the overlay is removed before
    // the callback fires so re-entrant commands (e.g. :refresh triggering
    // an OSD) don't race against a view that's about to be disposed.
    public static TextField Show(string seed, Action<string> onCommit)
    {
        var field = new TextField(seed)
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
            ColorScheme = Colors.Base
        };

        field.KeyPress += (View.KeyEventEventArgs k) =>
        {
            if (k.KeyEvent.Key == Key.Enter)
            {
                var text = field.Text.ToString() ?? "";
                field.SuperView?.Remove(field);
                onCommit(text);
                k.Handled = true;
            }
            else if (k.KeyEvent.Key == Key.Esc)
            {
                field.SuperView?.Remove(field);
                k.Handled = true;
            }
        };

        Application.Top.Add(field);
        field.SetFocus();
        field.CursorPosition = field.Text.ToString()!.Length;
        return field;
    }
}
