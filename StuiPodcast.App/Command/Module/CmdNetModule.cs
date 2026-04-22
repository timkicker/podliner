using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class CmdNetModule
{
    public static void ExecNet(string[] args, IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "online" or "on") { data.NetworkOnline = true; _ = persist(); ui.ShowOsd("Online", 600); }
        else if (arg is "offline" or "off") { data.NetworkOnline = false; _ = persist(); ui.ShowOsd("Offline", 600); }
        else if (string.IsNullOrEmpty(arg) || arg == "toggle") { data.NetworkOnline = !data.NetworkOnline; _ = persist(); ui.ShowOsd(data.NetworkOnline ? "Online" : "Offline", 600); }
        else { ui.ShowOsd("usage: :net online|offline|toggle", 1200); }

        CmdViewModule.ApplyList(ui, data, episodes);
        ui.RefreshEpisodesForSelectedFeed(episodes.Snapshot());

        var nowId = ui.GetNowPlayingId();
        if (nowId != null)
        {
            var playing = episodes.Find(nowId.Value);
            if (playing != null)
                ui.SetWindowTitle((!data.NetworkOnline ? "[OFFLINE] " : "") + (playing.Title ?? "—"));
        }
    }

    public static void ExecPlaySource(string[] args, IUiShell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "show" or "") { ui.ShowOsd($"play-source: {data.PlaySource ?? "auto"}"); return; }

        if (arg is "auto" or "local" or "remote") { data.PlaySource = arg; _ = persist(); ui.ShowOsd($"play-source: {arg}"); }
        else ui.ShowOsd("usage: :play-source auto|local|remote|show");
    }
}
