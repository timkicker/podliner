using StuiPodcast.App.Services;
using StuiPodcast.App.UI;

namespace StuiPodcast.App.Command.UseCases;

// :sleep [<duration>|off|status]
// Sets / cancels / shows the single-shot sleep timer. Duration grammar
// matches what most media players accept ("30m", "1h30m", "45s", bare
// number = minutes).
internal sealed class SleepUseCase
{
    readonly IUiShell _ui;
    readonly SleepTimer _timer;

    public SleepUseCase(IUiShell ui, SleepTimer timer)
    {
        _ui    = ui;
        _timer = timer;
    }

    public void Exec(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg == "off" || arg == "cancel" || arg == "stop")
        {
            if (!_timer.IsActive) { _ui.ShowOsd("sleep: no timer set", 1200); return; }
            _timer.Cancel();
            _ui.ShowOsd("sleep: timer cancelled", 1200);
            return;
        }

        if (arg == "" || arg == "status")
        {
            var left = _timer.TimeLeft;
            if (left is null || left.Value <= TimeSpan.Zero)
            {
                _ui.ShowOsd("sleep: no timer set — usage: :sleep <30m|1h|off>", 2000);
                return;
            }
            _ui.ShowOsd($"sleep: {SleepTimer.FormatDuration(left.Value)} remaining", 1500);
            return;
        }

        var duration = SleepTimer.ParseDuration(arg);
        if (duration is null)
        {
            _ui.ShowOsd($"sleep: bad duration '{arg}' — try 30m, 1h30m, 90s", 2000);
            return;
        }

        _timer.Set(duration.Value);
        _ui.ShowOsd($"sleep: timer set for {SleepTimer.FormatDuration(duration.Value)}", 1500);
    }
}
