using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class QueueModule
{
    public static bool HandleQueue(string cmd, Shell ui, AppData data, Func<Task> saveAsync)
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
            ui.SetQueueOrder(data.Queue);
            ui.RefreshEpisodesForSelectedFeed(data.Episodes);
        }
        async Task PersistLocal() { try { await saveAsync(); } catch { } }

        var ep = ui.GetSelectedEpisode();

        switch (sub)
        {
            case "add":
            case "toggle":
                if (ep == null) return true;
                if (data.Queue.Contains(ep.Id)) data.Queue.Remove(ep.Id);
                else data.Queue.Add(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "rm":
            case "remove":
                if (ep == null) return true;
                data.Queue.Remove(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "clear":
                data.Queue.Clear();
                Refresh(); _ = PersistLocal(); return true;

            case "shuffle":
            {
                var rnd = new Random();
                for (int i = data.Queue.Count - 1; i > 0; i--)
                { int j = rnd.Next(i + 1); (data.Queue[i], data.Queue[j]) = (data.Queue[j], data.Queue[i]); }
                Refresh(); _ = PersistLocal(); ui.ShowOsd("queue: shuffled", 900); return true;
            }
            case "uniq":
            {
                var seen = new HashSet<Guid>();
                var compact = new List<Guid>(data.Queue.Count);
                foreach (var id in data.Queue) if (seen.Add(id)) compact.Add(id);
                data.Queue.Clear(); data.Queue.AddRange(compact);
                Refresh(); _ = PersistLocal(); ui.ShowOsd("queue: uniq", 900); return true;
            }

            case "move":
            {
                var dir = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "down";
                var sel = ui.GetSelectedEpisode(); if (sel == null) return true;

                int idx = data.Queue.FindIndex(id => id == sel.Id);
                if (idx < 0) return true;

                int last = data.Queue.Count - 1;
                int target = idx;
                if (dir == "up") target = Math.Max(0, idx - 1);
                else if (dir == "down") target = Math.Min(last, idx + 1);
                else if (dir == "top") target = 0;
                else if (dir == "bottom") target = last;

                if (target != idx)
                {
                    var id = data.Queue[idx];
                    data.Queue.RemoveAt(idx);
                    data.Queue.Insert(target, id);
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
