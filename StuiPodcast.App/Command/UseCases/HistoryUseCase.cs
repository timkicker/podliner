using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.UseCases;

// Clears the play-history timestamps or adjusts the persisted history
// window. Relies on ViewUseCase to refresh the episode pane after a
// mutation so history views reflect the new state immediately.
internal sealed class HistoryUseCase
{
    readonly IUiShell _ui;
    readonly AppData _data;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;
    readonly ViewUseCase _view;

    public HistoryUseCase(IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes, ViewUseCase view)
    {
        _ui = ui;
        _data = data;
        _persist = persist;
        _episodes = episodes;
        _view = view;
    }

    public void Exec(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg.StartsWith("clear"))
        {
            int count = 0;
            foreach (var e in _episodes.Snapshot())
            {
                if (e.Progress.LastPlayedAt != null) { e.Progress.LastPlayedAt = null; count++; }
            }
            _ = _persist();
            _view.ApplyList();
            _ui.ShowOsd($"History cleared ({count})");
            return;
        }

        if (arg.StartsWith("size"))
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var n) && n > 0)
            {
                _data.HistorySize = Math.Clamp(n, 10, 10000);
                _ = _persist();

                _ui.SetHistoryLimit(_data.HistorySize);
                _view.ApplyList();
                _ui.ShowOsd($"History size = {_data.HistorySize}");
                return;
            }
            _ui.ShowOsd("usage: :history size <n>");
            return;
        }

        _ui.ShowOsd("history: clear | size <n>");
    }
}
