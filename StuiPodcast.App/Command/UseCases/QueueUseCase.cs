using StuiPodcast.App.Services;
using StuiPodcast.App.UI;

namespace StuiPodcast.App.Command.UseCases;

// Handles :queue sub-commands. Returns true when the input matched a queue
// verb so the command router fastpath can short-circuit (q = :queue add).
// All mutations flow through IQueueService so the snapshot cache and the
// Changed event stay coherent.
internal sealed class QueueUseCase
{
    readonly IUiShell _ui;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;
    readonly IQueueService _queue;

    public QueueUseCase(IUiShell ui, Func<Task> persist, IEpisodeStore episodes, IQueueService queue)
    {
        _ui = ui;
        _persist = persist;
        _episodes = episodes;
        _queue = queue;
    }

    public bool Handle(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var t = cmd.Trim();

        if (!t.StartsWith(":queue", StringComparison.OrdinalIgnoreCase) &&
            !t.Equals("q", StringComparison.OrdinalIgnoreCase)) return false;

        if (t.Equals("q", StringComparison.OrdinalIgnoreCase)) t = ":queue add";

        string[] parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "add";

        var ep = _ui.GetSelectedEpisode();

        switch (sub)
        {
            case "add":
            case "toggle":
                if (ep == null) return true;
                _queue.Toggle(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "rm":
            case "remove":
                if (ep == null) return true;
                _queue.Remove(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "clear":
                _queue.Clear();
                Refresh(); _ = PersistLocal(); return true;

            case "shuffle":
                _queue.Shuffle();
                Refresh(); _ = PersistLocal(); _ui.ShowOsd("queue: shuffled", 900); return true;

            case "uniq":
                _queue.Dedup();
                Refresh(); _ = PersistLocal(); _ui.ShowOsd("queue: uniq", 900); return true;

            case "move":
            {
                var dir = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "down";
                var sel = _ui.GetSelectedEpisode(); if (sel == null) return true;

                int idx = _queue.IndexOf(sel.Id);
                if (idx < 0) return true;

                int last = _queue.Count - 1;
                int target = idx;
                if (dir == "up") target = Math.Max(0, idx - 1);
                else if (dir == "down") target = Math.Min(last, idx + 1);
                else if (dir == "top") target = 0;
                else if (dir == "bottom") target = last;

                if (target != idx)
                {
                    _queue.Move(sel.Id, target);
                    Refresh(); _ = PersistLocal();
                    _ui.ShowOsd(target < idx ? "Moved ↑" : "Moved ↓");
                }
                return true;
            }

            default:
                return true;
        }
    }

    void Refresh()
    {
        _ui.SetQueueOrder(_queue.Snapshot());
        _ui.RefreshEpisodesForSelectedFeed(_episodes.Snapshot());
    }

    async Task PersistLocal()
    {
        try { await _persist(); } catch { }
    }
}
