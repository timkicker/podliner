using StuiPodcast.App.Debug;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

// Modal dialog that renders the last N lines from the in-memory log sink.
// Closes on F12 or Esc. Extracted from UiShell so the dialog layout lives
// alongside the other dialog helpers rather than lost in a ~1200-line shell.
internal static class UiShellLogsDialog
{
    public static void Show(MemoryLogSink mem, int tail = 500)
    {
        try
        {
            var lines = mem.Snapshot(tail);
            var dlg = new Dialog($"Logs (last {tail}) — F12/Esc to close", 100, 30);
            var tv = new TextView
            {
                ReadOnly = true, X = 0, Y = 0,
                Width = Dim.Fill(), Height = Dim.Fill(),
                WordWrap = false,
                Text = string.Join('\n', lines)
            };
            tv.MoveEnd();

            dlg.KeyPress += (View.KeyEventEventArgs e) =>
            {
                if (e.KeyEvent.Key == Key.F12 || e.KeyEvent.Key == Key.Esc)
                {
                    Application.RequestStop();
                    e.Handled = true;
                }
            };
            dlg.Add(tv);
            Application.Run(dlg);
        }
        catch { }
    }
}
