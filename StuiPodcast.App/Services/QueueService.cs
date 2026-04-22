using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Services;

// Thread-safe queue implementation backed by both AppData.Queue (runtime
// view) and LibraryStore.Current.Queue (persisted copy). During the
// migration both paths stay in sync so legacy code still reading from
// AppData.Queue keeps observing the right state.
internal sealed class QueueService : IQueueService
{
    readonly AppData _data;
    readonly LibraryStore _lib;
    readonly object _gate = new();
    IReadOnlyList<Guid>? _snapshot;

    public event Action? Changed;

    public QueueService(AppData data, LibraryStore lib)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _lib  = lib  ?? throw new ArgumentNullException(nameof(lib));
    }

    public int Count
    {
        get { lock (_gate) return _data.Queue.Count; }
    }

    public IReadOnlyList<Guid> Snapshot()
    {
        lock (_gate) return _snapshot ??= _data.Queue.ToArray();
    }

    public bool Contains(Guid id)
    {
        lock (_gate) return _data.Queue.Contains(id);
    }

    public int IndexOf(Guid id)
    {
        lock (_gate) return _data.Queue.IndexOf(id);
    }

    public bool Append(Guid id)
    {
        bool changed;
        lock (_gate)
        {
            if (_data.Queue.Contains(id)) return false;
            _data.Queue.Add(id);
            SyncToLib_Locked();
            _snapshot = null;
            changed = true;
        }
        if (changed) Fire();
        return changed;
    }

    public bool Toggle(Guid id)
    {
        lock (_gate)
        {
            if (_data.Queue.Remove(id)) { SyncToLib_Locked(); _snapshot = null; goto done; }
            _data.Queue.Add(id);
            SyncToLib_Locked();
            _snapshot = null;
        }
        done:
        Fire();
        return true;
    }

    public bool Remove(Guid id)
    {
        bool changed;
        lock (_gate)
        {
            changed = _data.Queue.Remove(id);
            if (changed) { SyncToLib_Locked(); _snapshot = null; }
        }
        if (changed) Fire();
        return changed;
    }

    public bool MoveToFront(Guid id)
    {
        bool changed;
        lock (_gate)
        {
            _data.Queue.Remove(id);
            _data.Queue.Insert(0, id);
            SyncToLib_Locked();
            _snapshot = null;
            changed = true;
        }
        Fire();
        return changed;
    }

    public bool Move(Guid id, int toIndex)
    {
        bool changed;
        lock (_gate)
        {
            int from = _data.Queue.IndexOf(id);
            if (from < 0) return false;
            int last = _data.Queue.Count - 1;
            int target = Math.Clamp(toIndex, 0, last);
            if (target == from) return false;
            _data.Queue.RemoveAt(from);
            _data.Queue.Insert(target, id);
            SyncToLib_Locked();
            _snapshot = null;
            changed = true;
        }
        Fire();
        return changed;
    }

    public int Clear()
    {
        int n;
        lock (_gate)
        {
            n = _data.Queue.Count;
            if (n == 0) return 0;
            _data.Queue.Clear();
            SyncToLib_Locked();
            _snapshot = null;
        }
        Fire();
        return n;
    }

    public int Dedup()
    {
        int removed;
        lock (_gate)
        {
            var seen = new HashSet<Guid>();
            var compact = new List<Guid>(_data.Queue.Count);
            foreach (var id in _data.Queue)
                if (seen.Add(id)) compact.Add(id);
            removed = _data.Queue.Count - compact.Count;
            if (removed == 0) return 0;
            _data.Queue.Clear();
            _data.Queue.AddRange(compact);
            SyncToLib_Locked();
            _snapshot = null;
        }
        Fire();
        return removed;
    }

    public int Shuffle()
    {
        int n;
        lock (_gate)
        {
            n = _data.Queue.Count;
            if (n <= 1) return 0;
            var rnd = new Random();
            for (int i = _data.Queue.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (_data.Queue[i], _data.Queue[j]) = (_data.Queue[j], _data.Queue[i]);
            }
            SyncToLib_Locked();
            _snapshot = null;
        }
        Fire();
        return n;
    }

    public bool TrimUpToInclusive(Guid targetId)
    {
        bool changed;
        lock (_gate)
        {
            int idx = _data.Queue.IndexOf(targetId);
            if (idx < 0) return false;
            _data.Queue.RemoveRange(0, idx + 1);
            SyncToLib_Locked();
            _snapshot = null;
            changed = true;
        }
        Fire();
        return changed;
    }

    // Mirror AppData.Queue into LibraryStore.Current.Queue so persisted
    // state matches. SaveAsync flushes on debounce.
    void SyncToLib_Locked()
    {
        _lib.Current.Queue.Clear();
        _lib.Current.Queue.AddRange(_data.Queue);
        _lib.SaveAsync();
    }

    void Fire()
    {
        try { Changed?.Invoke(); } catch { /* swallow */ }
    }
}
