using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.UseCases;

// Controls the user-visible online/offline flag and the play-source
// preference (auto/local/remote). Does not perform real network probes —
// those are handled by NetworkMonitor.
internal sealed class NetUseCase
{
    readonly IUiShell _ui;
    readonly AppData _data;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;
    readonly ViewUseCase _view;

    public NetUseCase(IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes, ViewUseCase view)
    {
        _ui = ui;
        _data = data;
        _persist = persist;
        _episodes = episodes;
        _view = view;
    }

    public void ExecNet(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "online" or "on") { _data.NetworkOnline = true; _ = _persist(); _ui.ShowOsd("Online", 600); }
        else if (arg is "offline" or "off") { _data.NetworkOnline = false; _ = _persist(); _ui.ShowOsd("Offline", 600); }
        else if (string.IsNullOrEmpty(arg) || arg == "toggle") { _data.NetworkOnline = !_data.NetworkOnline; _ = _persist(); _ui.ShowOsd(_data.NetworkOnline ? "Online" : "Offline", 600); }
        else { _ui.ShowOsd("usage: :net online|offline|toggle", 1200); }

        _view.ApplyList();
        _ui.RefreshEpisodesForSelectedFeed(_episodes.Snapshot());

        var nowId = _ui.GetNowPlayingId();
        if (nowId != null)
        {
            var playing = _episodes.Find(nowId.Value);
            if (playing != null)
                _ui.SetWindowTitle((!_data.NetworkOnline ? "[OFFLINE] " : "") + (playing.Title ?? "—"));
        }
    }

    public void ExecPlaySource(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "show" or "") { _ui.ShowOsd($"play-source: {_data.PlaySource ?? "auto"}"); return; }

        if (arg is "auto" or "local" or "remote") { _data.PlaySource = arg; _ = _persist(); _ui.ShowOsd($"play-source: {arg}"); }
        else _ui.ShowOsd("usage: :play-source auto|local|remote|show");
    }
}
