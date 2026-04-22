using StuiPodcast.App.Services;
using StuiPodcast.Core;

namespace StuiPodcast.App.Tests.Fakes;

// Pure in-memory episode store for unit tests. Avoids the disk roundtrip of
// a real LibraryStore while preserving the IEpisodeStore contract.
sealed class FakeEpisodeStore : IEpisodeStore
{
    private readonly Dictionary<Guid, Episode> _byId = new();
    private readonly List<Episode> _ordered = new();

    public event Action<EpisodeChange>? Changed;

    public int Count => _ordered.Count;
    public IReadOnlyList<Episode> Snapshot() => _ordered.ToArray();

    public bool TryGet(Guid id, out Episode? ep)
    {
        var ok = _byId.TryGetValue(id, out var e);
        ep = e;
        return ok;
    }

    public Episode? Find(Guid id) => _byId.TryGetValue(id, out var e) ? e : null;

    public IReadOnlyList<Episode> WhereByFeed(Guid feedId)
        => _ordered.Where(e => e.FeedId == feedId).ToArray();

    public Episode AddOrUpdate(Episode ep)
    {
        if (ep.Id == Guid.Empty) ep.Id = Guid.NewGuid();
        bool isNew = !_byId.ContainsKey(ep.Id);
        _byId[ep.Id] = ep;
        if (isNew) _ordered.Add(ep);
        Changed?.Invoke(new EpisodeChange(isNew ? EpisodeChangeKind.Added : EpisodeChangeKind.Updated, ep));
        return ep;
    }

    public bool Remove(Guid id)
    {
        if (!_byId.TryGetValue(id, out var ep)) return false;
        _byId.Remove(id);
        _ordered.RemoveAll(e => e.Id == id);
        Changed?.Invoke(new EpisodeChange(EpisodeChangeKind.Removed, ep));
        return true;
    }

    public int RemoveByFeed(Guid feedId)
    {
        var victims = _ordered.Where(e => e.FeedId == feedId).ToList();
        foreach (var v in victims) Remove(v.Id);
        return victims.Count;
    }

    public void SetProgress(Guid id, long posMs, DateTimeOffset? lastPlayedAt)
    {
        if (!_byId.TryGetValue(id, out var ep)) return;
        ep.Progress.LastPosMs = Math.Max(0, posMs);
        ep.Progress.LastPlayedAt = lastPlayedAt;
        Changed?.Invoke(new EpisodeChange(EpisodeChangeKind.Updated, ep));
    }

    public void SetSaved(Guid id, bool saved)
    {
        if (!_byId.TryGetValue(id, out var ep)) return;
        ep.Saved = saved;
        Changed?.Invoke(new EpisodeChange(EpisodeChangeKind.Updated, ep));
    }

    public void SetManuallyMarkedPlayed(Guid id, bool played)
    {
        if (!_byId.TryGetValue(id, out var ep)) return;
        ep.ManuallyMarkedPlayed = played;
        Changed?.Invoke(new EpisodeChange(EpisodeChangeKind.Updated, ep));
    }

    // Test convenience — seed multiple episodes without firing events.
    public void Seed(params Episode[] episodes)
    {
        foreach (var e in episodes)
        {
            if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
            _byId[e.Id] = e;
            _ordered.Add(e);
        }
    }
}
