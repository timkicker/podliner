using Podliner.App.Services;
using Podliner.App.UI;

namespace Podliner.App.Command.UseCases;

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
    readonly UndoStack? _undo;

    public QueueUseCase(IUiShell ui, Func<Task> persist, IEpisodeStore episodes, IQueueService queue, UndoStack? undo = null)
    {
        _ui = ui;
        _persist = persist;
        _episodes = episodes;
        _queue = queue;
        _undo = undo;
    }

    public bool Handle(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var t = cmd.Trim();

        // A bare "q" used to be accepted here as a shortcut for ":queue add",
        // but "q" is the quit key everywhere else in the app and in every
        // other terminal program. The shortcut is gone; ":queue add" stays.
        if (!t.StartsWith(":queue", StringComparison.OrdinalIgnoreCase)) return false;

        string[] parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "add";

        var ep = _ui.GetSelectedEpisode();

        switch (sub)
        {
            case "add":
            {
                // `add` used to be a second name for `toggle`, so running it
                // on an already-queued episode silently dropped it back out.
                if (ep == null) return true;
                if (_queue.Contains(ep.Id)) { _ui.ShowOsd("queue: already queued", 900); return true; }
                _queue.Append(ep.Id);
                Refresh(); _ = PersistLocal();
                _ui.ShowOsd("queue: added", 900);
                return true;
            }

            case "toggle":
            {
                if (ep == null) return true;
                var wasQueued = _queue.Contains(ep.Id);
                if (wasQueued) RemoveWithUndo(ep.Id);
                else
                {
                    _queue.Append(ep.Id);
                    Refresh(); _ = PersistLocal();
                }
                _ui.ShowOsd(wasQueued ? "queue: removed" : "queue: added", 900);
                return true;
            }

            case "rm":
            case "remove":
                if (ep == null) return true;
                if (!_queue.Contains(ep.Id)) { _ui.ShowOsd("queue: not queued", 900); return true; }
                RemoveWithUndo(ep.Id);
                _ui.ShowOsd("queue: removed", 900);
                return true;

            case "clear":
            {
                var snapshot = _queue.Snapshot().ToArray();
                _queue.Clear();
                Refresh(); _ = PersistLocal();
                if (snapshot.Length > 0 && _undo != null)
                {
                    _undo.Push($"restore queue ({snapshot.Length} items)", () =>
                    {
                        foreach (var id in snapshot) _queue.Append(id);
                        Refresh(); _ = PersistLocal();
                    });
                    _ui.ShowOsd($"queue cleared ({snapshot.Length}) — :undo to restore", 2000);
                }
                else
                {
                    _ui.ShowOsd("queue cleared", 1200);
                }
                return true;
            }

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

    // Dropping an episode out of the queue is a destructive action, and
    // :undo promises to revert those. Restores it at the index it held.
    void RemoveWithUndo(Guid id)
    {
        var index = _queue.IndexOf(id);
        _queue.Remove(id);
        Refresh(); _ = PersistLocal();

        if (index < 0 || _undo == null) return;
        _undo.Push("restore queued episode", () =>
        {
            if (_queue.Contains(id)) return;
            _queue.Append(id);
            _queue.Move(id, index);
            Refresh(); _ = PersistLocal();
        });
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
