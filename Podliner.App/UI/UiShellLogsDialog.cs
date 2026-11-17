using Podliner.App.Debug;
using Terminal.Gui;

namespace Podliner.App.UI;

// Modal dialog that renders the last N lines from the in-memory log sink.
// Closes on F12 or Esc. Extracted from UiShell so the dialog layout lives
// alongside the other dialog helpers rather than lost in a ~1200-line shell.
internal static class UiShellLogsDialog
{
    public static void Show(MemoryLogSink mem, int tail = 500)
    {
        try { Application.Run(Build(mem, tail)); }
        catch { }
    }

    // Split out from Show so tests can render the dialog without the blocking
    // Application.Run.
    internal static Dialog Build(MemoryLogSink mem, int tail = 500)
    {
        {
            var lines = mem.Snapshot(tail);
            var dlg = new Dialog($"Logs (last {tail}) — F12/Esc to close", 100, 30);
            var tv = new LogTextView
            {
                ReadOnly = true, X = 0, Y = 0,
                Width = Dim.Fill(), Height = Dim.Fill(),
                WordWrap = false,
                Text = string.Join('\n', lines)
            };
            tv.MoveEnd();

            // MoveEnd parks the view on the last line whether or not the log
            // fills it, so a fresh session showed one line above 27 blank
            // rows. Pin the top once the frame is known instead.
            dlg.Loaded += () =>
            {
                var visible = Math.Max(1, tv.Frame.Height);
                tv.TopRow = Math.Max(0, lines.Length - visible);
                tv.SetNeedsDisplay();
            };

            dlg.KeyPress += (View.KeyEventEventArgs e) =>
            {
                if (e.KeyEvent.Key == Key.F12 || e.KeyEvent.Key == Key.Esc)
                {
                    Application.RequestStop();
                    e.Handled = true;
                }
            };
            dlg.Add(tv);
            return dlg;
        }
    }

    // Terminal.Gui's TextView lets the mouse wheel scroll until only the last
    // line is left at the top, so the log ran into a blank view (#42). Keys
    // were fine. Keep the wheel inside the text.
    internal sealed class LogTextView : TextView
    {
        public override bool MouseEvent(MouseEvent ev)
        {
            var handled = base.MouseEvent(ev);
            if (ev.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                var last = Math.Max(0, Lines - Math.Max(1, Frame.Height));
                if (TopRow > last)
                {
                    TopRow = last;
                    SetNeedsDisplay();
                }
            }
            return handled;
        }
    }
}
