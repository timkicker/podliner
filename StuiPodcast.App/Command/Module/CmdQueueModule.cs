using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class CmdQueueModule
{
    public static bool HandleQueue(string cmd, IUiShell ui, AppData data, Func<Task> saveAsync, IEpisodeStore episodes, IQueueService queue)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var t = cmd.Trim();

        if (!t.StartsWith(":queue", StringComparison.OrdinalIgnoreCase) &&
            !t.Equals("q", StringComparison.OrdinalIgnoreCase)) return false;

        if (t.Equals("q", StringComparison.OrdinalIgnoreCase)) t = ":queue add";

        string[] parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "add";

        void Refresh()
        {
            ui.SetQueueOrder(queue.Snapshot());
            ui.RefreshEpisodesForSelectedFeed(episodes.Snapshot());
        }
        async Task PersistLocal() { try { await saveAsync(); } catch { } }

        var ep = ui.GetSelectedEpisode();

        switch (sub)
        {
            case "add":
            case "toggle":
                if (ep == null) return true;
                queue.Toggle(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "rm":
            case "remove":
                if (ep == null) return true;
                queue.Remove(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "clear":
                queue.Clear();
                Refresh(); _ = PersistLocal(); return true;

            case "shuffle":
                queue.Shuffle();
                Refresh(); _ = PersistLocal(); ui.ShowOsd("queue: shuffled", 900); return true;

            case "uniq":
                queue.Dedup();
                Refresh(); _ = PersistLocal(); ui.ShowOsd("queue: uniq", 900); return true;

            case "move":
            {
                var dir = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "down";
                var sel = ui.GetSelectedEpisode(); if (sel == null) return true;

                int idx = queue.IndexOf(sel.Id);
                if (idx < 0) return true;

                int last = queue.Count - 1;
                int target = idx;
                if (dir == "up") target = Math.Max(0, idx - 1);
                else if (dir == "down") target = Math.Min(last, idx + 1);
                else if (dir == "top") target = 0;
                else if (dir == "bottom") target = last;

                if (target != idx)
                {
                    queue.Move(sel.Id, target);
                    Refresh(); _ = PersistLocal();
                    ui.ShowOsd(target < idx ? "Moved ↑" : "Moved ↓");
                }
                return true;
            }

            default:
                return true;
        }
    }
}
