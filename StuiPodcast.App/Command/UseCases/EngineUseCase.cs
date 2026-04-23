using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command.UseCases;

// :engine sub-commands — show/diag/help and live engine switching. The
// actual swap is delegated back via an injected callback so the hot-swap
// happens in one place (EngineService) instead of being duplicated here.
internal sealed class EngineUseCase
{
    readonly IAudioPlayer _audioPlayer;
    readonly IUiShell _ui;
    readonly AppData _data;
    readonly Func<Task> _persist;
    readonly Func<string, Task>? _switchEngine;

    public EngineUseCase(IAudioPlayer audioPlayer, IUiShell ui, AppData data, Func<Task> persist, Func<string, Task>? switchEngine)
    {
        _audioPlayer = audioPlayer;
        _ui = ui;
        _data = data;
        _persist = persist;
        _switchEngine = switchEngine;
    }

    public void Exec(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (args.Length > 0 && string.Equals(args[0], "diag", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var activeName = _audioPlayer?.Name;
                if (string.IsNullOrWhiteSpace(activeName))
                    activeName = _audioPlayer?.GetType().Name ?? "none";

                var caps = _audioPlayer?.Capabilities ?? 0;
                bool cSeek = (caps & PlayerCapabilities.Seek) != 0;
                bool cPause = (caps & PlayerCapabilities.Pause) != 0;
                bool cSpeed = (caps & PlayerCapabilities.Speed) != 0;
                bool cVolume = (caps & PlayerCapabilities.Volume) != 0;

                var pref = string.IsNullOrWhiteSpace(_data?.PreferredEngine) ? "auto" : _data!.PreferredEngine!;
                var last = _data?.LastEngineUsed ?? "";

                _ui.ShowOsd($"engine: {activeName}  caps: seek={cSeek} pause={cPause} speed={cSpeed} vol={cVolume}  pref={pref} last={last}", 3000);
            }
            catch (Exception ex) { _ui.ShowOsd($"engine: diag error ({ex.Message})", 2000); }
            return;
        }

        if (string.IsNullOrEmpty(arg) || arg == "show")
        {
            var caps = _audioPlayer.Capabilities;
            var txt = $"engine active: {_audioPlayer.Name}\n" +
                      $"preference: {_data.PreferredEngine ?? "auto"}\n" +
                      "supports: " +
                      $"{((caps & PlayerCapabilities.Seek) != 0 ? "seek " : "")}" +
                      $"{((caps & PlayerCapabilities.Speed) != 0 ? "speed " : "")}" +
                      $"{((caps & PlayerCapabilities.Volume) != 0 ? "volume " : "")}".Trim();
            _ui.ShowOsd(txt, 1500);
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
            _data.PreferredEngine = arg switch { "mf" => "mediafoundation", _ => arg };
            _ = _persist();

            if (_switchEngine != null)
            {
                _ui.ShowOsd($"engine: switching to {arg}…", 900);
                _ = _switchEngine(arg); // fire-and-forget
            }
            else
            {
                _ui.ShowOsd($"engine pref: {arg} (active: {_audioPlayer.Name})", 1500);
            }
            return;
        }

        _ui.ShowOsd("usage: :engine [show|help|auto|vlc|mpv|ffplay|mediafoundation]", 1500);
    }
}
