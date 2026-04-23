using StuiPodcast.App.UI;
using Terminal.Gui;

namespace StuiPodcast.App.Command.UseCases;

// App-level housekeeping commands: :logs (show tail of the log), :osd
// (diagnostic OSD), :w / :wq (save-now, save-and-quit). The save is run
// off the UI thread so the TUI stays responsive while the library json
// is written to disk.
internal sealed class SystemUseCase
{
    readonly IUiShell _ui;
    readonly Func<Task> _persist;

    public SystemUseCase(IUiShell ui, Func<Task> persist)
    {
        _ui = ui;
        _persist = persist;
    }

    public void ExecLogs(string[] args)
    {
        var a = args.Length > 0 ? args[0] : "";
        int tail = 500;
        if (int.TryParse(a, out var n) && n > 0) tail = Math.Min(n, 5000);
        _ui.ShowLogsOverlay(tail);
    }

    public void ExecOsd(string[] args)
    {
        var text = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(text)) _ui.ShowOsd(text);
        else _ui.ShowOsd("usage: :osd <text>");
    }

    public void ExecWrite()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _persist().ConfigureAwait(false);
                Application.MainLoop?.Invoke(() => _ui.ShowOsd("saved", 900));
            }
            catch (Exception ex)
            {
                Application.MainLoop?.Invoke(() => _ui.ShowOsd($"save failed: {ex.Message}", 1800));
            }
        });
    }

    public void ExecWriteQuit(bool bang)
    {
        _ui.ShowOsd(bang ? "saving… quitting!" : "saving… quitting…", 800);
        _ = Task.Run(async () =>
        {
            try { await _persist().ConfigureAwait(false); }
            catch
            {
                Application.MainLoop?.Invoke(() => _ui.ShowOsd("save failed – aborting :wq", 1800));
                return;
            }
            Application.MainLoop?.Invoke(() => _ui.RequestQuit());
        });
    }
}
