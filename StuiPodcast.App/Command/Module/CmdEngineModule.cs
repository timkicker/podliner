using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App;

internal static class CmdEngineModule
{
    public static void ExecEngine(
        string[] args,
        IAudioPlayer audioPlayer,
        UiShell ui,
        AppData data,
        Func<Task> persist,
        Func<string, Task>? switchEngine = null)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (args.Length > 0 && string.Equals(args[0], "diag", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var activeName = audioPlayer?.Name;
                if (string.IsNullOrWhiteSpace(activeName))
                    activeName = audioPlayer?.GetType().Name ?? "none";

                var caps = audioPlayer?.Capabilities ?? 0;
                bool cSeek = (caps & PlayerCapabilities.Seek) != 0;
                bool cPause = (caps & PlayerCapabilities.Pause) != 0;
                bool cSpeed = (caps & PlayerCapabilities.Speed) != 0;
                bool cVolume = (caps & PlayerCapabilities.Volume) != 0;

                var pref = string.IsNullOrWhiteSpace(data?.PreferredEngine) ? "auto" : data!.PreferredEngine!;
                var last = data?.LastEngineUsed ?? "";

                ui.ShowOsd($"engine: {activeName}  caps: seek={cSeek} pause={cPause} speed={cSpeed} vol={cVolume}  pref={pref} last={last}", 3000);
            }
            catch (Exception ex) { ui.ShowOsd($"engine: diag error ({ex.Message})", 2000); }
            return;
        }

        if (string.IsNullOrEmpty(arg) || arg == "show")
        {
            var caps = audioPlayer.Capabilities;
            var txt = $"engine active: {audioPlayer.Name}\n" +
                      $"preference: {data.PreferredEngine ?? "auto"}\n" +
                      "supports: " +
                      $"{((caps & PlayerCapabilities.Seek) != 0 ? "seek " : "")}" +
                      $"{((caps & PlayerCapabilities.Speed) != 0 ? "speed " : "")}" +
                      $"{((caps & PlayerCapabilities.Volume) != 0 ? "volume " : "")}".Trim();
            ui.ShowOsd(txt, 1500);
            return;
        }

        if (arg == "help")
        {
            try
            {
                var dlg = new Terminal.Gui.Dialog("Engine Help", 80, 24);
                var tv = new Terminal.Gui.TextView { ReadOnly = true, WordWrap = true, X = 0, Y = 0, Width = Terminal.Gui.Dim.Fill(), Height = Terminal.Gui.Dim.Fill() };
                tv.Text = StuiPodcast.App.HelpCatalog.EngineDoc;
                dlg.Add(tv);
                var ok = new Terminal.Gui.Button("OK", is_default: true);
                ok.Clicked += () => Terminal.Gui.Application.RequestStop();
                dlg.AddButton(ok);
                Terminal.Gui.Application.Run(dlg);
            }
            catch { }
            return;
        }

        if (arg is "auto" or "vlc" or "mpv" or "ffplay" or "mediafoundation" or "mf")
        {
            data.PreferredEngine = arg switch { "mf" => "mediafoundation", _ => arg };
            _ = persist();

            if (switchEngine != null)
            {
                ui.ShowOsd($"engine: switching to {arg}…", 900);
                _ = switchEngine(arg); // fire-and-forget
            }
            else
            {
                ui.ShowOsd($"engine pref: {arg} (active: {audioPlayer.Name})", 1500);
            }
            return;
        }

        ui.ShowOsd("usage: :engine [show|help|auto|vlc|mpv|ffplay|mediafoundation]", 1500);
    }
}