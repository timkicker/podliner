using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class CmdStateModule
{
    public static void ExecSave(string[] args, UiShell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        SaveToggle(arg, ui, data, persist);
    }

    private static void SaveToggle(string arg, UiShell ui, AppData data, Func<Task> persist)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool newVal = ep.Saved;

        if (arg is "on" or "true" or "+") newVal = true;
        else if (arg is "off" or "false" or "-") newVal = false;
        else newVal = !ep.Saved;

        ep.Saved = newVal;
        _ = persist();

        CmdViewModule.ApplyList(ui, data);
        ui.ShowOsd(newVal ? "Saved ★" : "Unsaved");
    }
}
