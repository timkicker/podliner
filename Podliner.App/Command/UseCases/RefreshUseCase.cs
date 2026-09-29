using Podliner.App.Services;
using Podliner.App.UI;
using Podliner.Core;

namespace Podliner.App.Command.UseCases;

// :refresh                      fetch every feed now
// :refresh auto                 show the interval and when the last pass ran
// :refresh auto <minutes>|off   set how often feeds are fetched on their own
//
// The auto half is #32; before it, :refresh was the only way anything was
// ever fetched. Same shape as :sync auto.
internal sealed class RefreshUseCase
{
    const string Usage = "usage: :refresh [auto [<minutes>|off]]";

    readonly IUiShell _ui;
    readonly AppData _data;
    readonly Func<Task> _persist;
    readonly Func<DateTimeOffset> _clock;

    public RefreshUseCase(IUiShell ui, AppData data, Func<Task> persist, Func<DateTimeOffset>? clock = null)
    {
        _ui = ui;
        _data = data;
        _persist = persist;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public void Exec(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _ui.ShowOsd("Refreshing…", 600);
            _ui.RequestRefresh();
            return;
        }

        if (args.Length == 1) { ShowStatus(); return; }

        var raw = args[1].Trim().ToLowerInvariant();
        if (raw == "off") raw = "0";
        if (raw.EndsWith('m')) raw = raw[..^1];

        if (!int.TryParse(raw, out var asked) || asked < 0)
        {
            _ui.ShowOsd(Usage, 2000);
            return;
        }

        var minutes = FeedRefreshInterval.Normalize(asked);
        _data.RefreshIntervalMinutes = minutes;
        _ = _persist();

        if (minutes == 0)
            _ui.ShowOsd("refresh: automatic refresh off", 1500);
        else if (minutes > asked)
            _ui.ShowOsd($"refresh: every {minutes}m (the shortest allowed)", 2000);
        else if (minutes < asked)
            _ui.ShowOsd($"refresh: every {minutes}m (the longest allowed)", 2000);
        else
            _ui.ShowOsd($"refresh: every {minutes}m", 1500);
    }

    void ShowStatus()
    {
        var last = FeedRefreshPolicy.FormatAge(_data.LastRefreshAt, _clock());
        var interval = _data.RefreshIntervalMinutes > 0
            ? $"every {_data.RefreshIntervalMinutes}m"
            : "automatic refresh off";
        _ui.ShowOsd($"refresh: {interval}, last {last}", 2500);
    }
}
