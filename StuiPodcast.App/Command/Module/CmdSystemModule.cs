using StuiPodcast.App.UI;

namespace StuiPodcast.App.Command.Module;

internal static class CmdSystemModule
{
    public static void ExecLogs(string[] args, UiShell ui)
    {
        var a = args.Length > 0 ? args[0] : "";
        int tail = 500;
        if (int.TryParse(a, out var n) && n > 0) tail = Math.Min(n, 5000);
        ui.ShowLogsOverlay(tail);
    }

    public static void ExecOsd(string[] args, UiShell ui)
    {
        var text = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(text)) ui.ShowOsd(text);
        else ui.ShowOsd("usage: :osd <text>");
    }

    public static void ExecWrite(Func<Task> persist, UiShell ui)
    {
        try { persist().GetAwaiter().GetResult(); ui.ShowOsd("saved", 900); }
        catch (Exception ex) { ui.ShowOsd($"save failed: {ex.Message}", 1800); }
    }

    public static void ExecWriteQuit(Func<Task> persist, UiShell ui, bool bang)
    {
        ui.ShowOsd(bang ? "saving… quitting!" : "saving… quitting…", 800);
        _ = Task.Run(async () =>
        {
            try { await persist().ConfigureAwait(false); }
            catch
            {
                Terminal.Gui.Application.MainLoop?.Invoke(() => ui.ShowOsd("save failed – aborting :wq", 1800));
                return;
            }
            Terminal.Gui.Application.MainLoop?.Invoke(() => ui.RequestQuit());
        });
    }
}