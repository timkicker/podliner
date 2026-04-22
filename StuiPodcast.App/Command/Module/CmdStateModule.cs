using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class CmdStateModule
{
    public static void ExecSave(string[] args, IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        SaveToggle(arg, ui, data, persist, episodes);
    }

    private static void SaveToggle(string arg, IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool newVal = ep.Saved;

        if (arg is "on" or "true" or "+") newVal = true;
        else if (arg is "off" or "false" or "-") newVal = false;
        else newVal = !ep.Saved;

        episodes.SetSaved(ep.Id, newVal);
        _ = persist();

        CmdViewModule.ApplyList(ui, data, episodes);
        ui.ShowOsd(newVal ? "Saved ★" : "Unsaved");
    }
}
