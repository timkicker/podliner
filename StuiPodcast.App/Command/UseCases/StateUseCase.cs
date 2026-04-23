using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.UseCases;

// Toggles the per-episode Saved flag. ApplyList is re-triggered so the
// saved virtual feed reflects the change immediately.
internal sealed class StateUseCase
{
    readonly IUiShell _ui;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;
    readonly ViewUseCase _view;

    public StateUseCase(IUiShell ui, Func<Task> persist, IEpisodeStore episodes, ViewUseCase view)
    {
        _ui = ui;
        _persist = persist;
        _episodes = episodes;
        _view = view;
    }

    public void ExecSave(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        var ep = _ui.GetSelectedEpisode();
        if (ep is null) return;

        bool newVal = ep.Saved;
        if (arg is "on" or "true" or "+") newVal = true;
        else if (arg is "off" or "false" or "-") newVal = false;
        else newVal = !ep.Saved;

        _episodes.SetSaved(ep.Id, newVal);
        _ = _persist();

        _view.ApplyList();
        _ui.ShowOsd(newVal ? "Saved ★" : "Unsaved");
    }
}
