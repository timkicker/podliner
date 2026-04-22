using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

internal static class CmdQueueModule
{
    // When an IQueueService is supplied we route every mutation through it —
    // a single owner means the snapshot cache + Changed event stay coherent
    // and the legacy `data.Queue.Add/Remove` path can go away once all
    // readers have migrated. Falls back to direct mutations when null so
    // existing tests that don't wire a queue service keep working.
    public static bool HandleQueue(string cmd, IUiShell ui, AppData data, Func<Task> saveAsync, IQueueService? queue = null)
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
                if (queue != null) queue.Toggle(ep.Id);
                else
                {
                    if (data.Queue.Contains(ep.Id)) data.Queue.Remove(ep.Id);
                    else data.Queue.Add(ep.Id);
                }
                Refresh(); _ = PersistLocal(); return true;

            case "rm":
            case "remove":
                if (ep == null) return true;
                if (queue != null) queue.Remove(ep.Id);
                else data.Queue.Remove(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "clear":
                if (queue != null) queue.Clear();
                else data.Queue.Clear();
                Refresh(); _ = PersistLocal(); return true;

            case "shuffle":
            {
                if (queue != null) queue.Shuffle();
                else
                {
                    var rnd = new Random();
                    for (int i = data.Queue.Count - 1; i > 0; i--)
                    { int j = rnd.Next(i + 1); (data.Queue[i], data.Queue[j]) = (data.Queue[j], data.Queue[i]); }
                }
                Refresh(); _ = PersistLocal(); ui.ShowOsd("queue: shuffled", 900); return true;
            }
            case "uniq":
            {
                if (queue != null) queue.Dedup();
                else
                {
                    var seen = new HashSet<Guid>();
                    var compact = new List<Guid>(data.Queue.Count);
                    foreach (var id in data.Queue) if (seen.Add(id)) compact.Add(id);
                    data.Queue.Clear(); data.Queue.AddRange(compact);
                }
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
                    if (queue != null)
                    {
                        queue.Move(sel.Id, target);
                    }
                    else
                    {
                        var id = data.Queue[idx];
                        data.Queue.RemoveAt(idx);
                        data.Queue.Insert(target, id);
                    }
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
