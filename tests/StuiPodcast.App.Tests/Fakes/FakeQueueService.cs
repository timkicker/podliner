using StuiPodcast.App.Services;

namespace StuiPodcast.App.Tests.Fakes;

sealed class FakeQueueService : IQueueService
{
    private readonly List<Guid> _ids = new();

    public event Action? Changed;

    public int Count => _ids.Count;
    public IReadOnlyList<Guid> Snapshot() => _ids.ToArray();
    public bool Contains(Guid id) => _ids.Contains(id);
    public int IndexOf(Guid id) => _ids.IndexOf(id);

    public bool Append(Guid id)
    {
        if (_ids.Contains(id)) return false;
        _ids.Add(id);
        Changed?.Invoke();
        return true;
    }

    public bool Toggle(Guid id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        Changed?.Invoke();
        return true;
    }

    public bool Remove(Guid id)
    {
        var changed = _ids.Remove(id);
        if (changed) Changed?.Invoke();
        return changed;
    }

    public bool MoveToFront(Guid id)
    {
        _ids.Remove(id);
        _ids.Insert(0, id);
        Changed?.Invoke();
        return true;
    }

    public bool Move(Guid id, int toIndex)
    {
        int from = _ids.IndexOf(id);
        if (from < 0) return false;
        int target = Math.Clamp(toIndex, 0, _ids.Count - 1);
        if (target == from) return false;
        _ids.RemoveAt(from);
        _ids.Insert(target, id);
        Changed?.Invoke();
        return true;
    }

    public int Clear()
    {
        int n = _ids.Count;
        if (n == 0) return 0;
        _ids.Clear();
        Changed?.Invoke();
        return n;
    }

    public int Dedup()
    {
        var seen = new HashSet<Guid>();
        var compact = new List<Guid>(_ids.Count);
        foreach (var id in _ids) if (seen.Add(id)) compact.Add(id);
        int removed = _ids.Count - compact.Count;
        if (removed == 0) return 0;
        _ids.Clear(); _ids.AddRange(compact);
        Changed?.Invoke();
        return removed;
    }

    public int Shuffle()
    {
        if (_ids.Count <= 1) return 0;
        var rnd = new Random();
        for (int i = _ids.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
        }
        Changed?.Invoke();
        return _ids.Count;
    }

    public bool TrimUpToInclusive(Guid targetId)
    {
        int idx = _ids.IndexOf(targetId);
        if (idx < 0) return false;
        _ids.RemoveRange(0, idx + 1);
        Changed?.Invoke();
        return true;
    }

    // Test convenience
    public void Seed(params Guid[] ids) => _ids.AddRange(ids);
}
