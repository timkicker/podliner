using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App;

internal static class StateModule
{
    public static void ExecSave(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        SaveToggle(arg, ui, data, persist);
    }

    private static void SaveToggle(string arg, Shell ui, AppData data, Func<Task> persist)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool newVal = ep.Saved;

        if (arg is "on" or "true" or "+") newVal = true;
        else if (arg is "off" or "false" or "-") newVal = false;
        else newVal = !ep.Saved;

        ep.Saved = newVal;
        _ = persist();

        ViewModule.ApplyList(ui, data);
        ui.ShowOsd(newVal ? "Saved ★" : "Unsaved");
    }
}
