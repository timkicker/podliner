using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Services;

// Thread-safe queue backed directly by LibraryStore.Current.Queue — the same
// pattern as EpisodeStore and FeedStore. Single source of truth; mutations
// trigger a debounced save via SaveAsync.
internal sealed class QueueService : IQueueService
{
    readonly LibraryStore _lib;
    readonly object _gate = new();
    IReadOnlyList<Guid>? _snapshot;

    public event Action? Changed;

    public QueueService(LibraryStore lib)
    {
        _lib = lib ?? throw new ArgumentNullException(nameof(lib));
    }

    public int Count
    {
        get { lock (_gate) return _lib.Current.Queue.Count; }
    }

    public IReadOnlyList<Guid> Snapshot()
    {
        lock (_gate) return _snapshot ??= _lib.Current.Queue.ToArray();
    }

    public bool Contains(Guid id)
    {
        lock (_gate) return _lib.Current.Queue.Contains(id);
    }

    public int IndexOf(Guid id)
    {
        lock (_gate) return _lib.Current.Queue.IndexOf(id);
    }

    public bool Append(Guid id)
    {
        bool changed;
        lock (_gate)
        {
            if (_lib.Current.Queue.Contains(id)) return false;
            _lib.Current.Queue.Add(id);
            _lib.SaveAsync();
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
            if (_lib.Current.Queue.Remove(id)) { _lib.SaveAsync(); _snapshot = null; goto done; }
            _lib.Current.Queue.Add(id);
            _lib.SaveAsync();
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
            changed = _lib.Current.Queue.Remove(id);
            if (changed) { _lib.SaveAsync(); _snapshot = null; }
        }
        if (changed) Fire();
        return changed;
    }

    public bool MoveToFront(Guid id)
    {
        lock (_gate)
        {
            _lib.Current.Queue.Remove(id);
            _lib.Current.Queue.Insert(0, id);
            _lib.SaveAsync();
            _snapshot = null;
        }
        Fire();
        return true;
    }

    public bool Move(Guid id, int toIndex)
    {
        bool changed;
        lock (_gate)
        {
            int from = _lib.Current.Queue.IndexOf(id);
            if (from < 0) return false;
            int last = _lib.Current.Queue.Count - 1;
            int target = Math.Clamp(toIndex, 0, last);
            if (target == from) return false;
            _lib.Current.Queue.RemoveAt(from);
            _lib.Current.Queue.Insert(target, id);
            _lib.SaveAsync();
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
            n = _lib.Current.Queue.Count;
            if (n == 0) return 0;
            _lib.Current.Queue.Clear();
            _lib.SaveAsync();
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
            var compact = new List<Guid>(_lib.Current.Queue.Count);
            foreach (var id in _lib.Current.Queue)
                if (seen.Add(id)) compact.Add(id);
            removed = _lib.Current.Queue.Count - compact.Count;
            if (removed == 0) return 0;
            _lib.Current.Queue.Clear();
            _lib.Current.Queue.AddRange(compact);
            _lib.SaveAsync();
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
            n = _lib.Current.Queue.Count;
            if (n <= 1) return 0;
            var rnd = new Random();
            for (int i = _lib.Current.Queue.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (_lib.Current.Queue[i], _lib.Current.Queue[j]) = (_lib.Current.Queue[j], _lib.Current.Queue[i]);
            }
            _lib.SaveAsync();
            _snapshot = null;
        }
        Fire();
        return n;
    }

    public bool TrimUpToInclusive(Guid targetId)
    {
        lock (_gate)
        {
            int idx = _lib.Current.Queue.IndexOf(targetId);
            if (idx < 0) return false;
            _lib.Current.Queue.RemoveRange(0, idx + 1);
            _lib.SaveAsync();
            _snapshot = null;
        }
        Fire();
        return true;
    }

    void Fire()
    {
        try { Changed?.Invoke(); } catch { /* swallow */ }
    }
}
