using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App;

internal static class HistoryModule
{
    public static void ExecHistory(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg.StartsWith("clear"))
        {
            int count = 0;
            foreach (var e in data.Episodes)
            {
                if (e.Progress.LastPlayedAt != null) { e.Progress.LastPlayedAt = null; count++; }
            }
            _ = persist();
            ViewModule.ApplyList(ui, data);
            ui.ShowOsd($"History cleared ({count})");
            return;
        }

        if (arg.StartsWith("size"))
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var n) && n > 0)
            {
                data.HistorySize = Math.Clamp(n, 10, 10000);
                _ = persist();

                ui.SetHistoryLimit(data.HistorySize);
                ViewModule.ApplyList(ui, data);
                ui.ShowOsd($"History size = {data.HistorySize}");
                return;
            }
            ui.ShowOsd("usage: :history size <n>");
            return;
        }

        ui.ShowOsd("history: clear | size <n>");
    }
}
