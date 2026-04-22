using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Services;

// Thread-safe facade over LibraryStore for episode data. Everything is
// delegated to the underlying store so this class is never out-of-sync with
// persistence. A lock around reads/writes coalesces mutations from the UI
// thread and background tasks (feed refresh, gpodder pull) that used to
// race on `AppData.Episodes`. Snapshots are cached until the next mutation.
internal sealed class EpisodeStore : IEpisodeStore
{
    readonly LibraryStore _lib;
    readonly object _gate = new();
    IReadOnlyList<Episode>? _snapshot;

    public event Action<EpisodeChange>? Changed;

    public EpisodeStore(LibraryStore lib)
    {
        _lib = lib ?? throw new ArgumentNullException(nameof(lib));
    }

    public int Count
    {
        get { lock (_gate) return _lib.Current.Episodes.Count; }
    }

    public IReadOnlyList<Episode> Snapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= _lib.Current.Episodes.ToArray();
        }
    }

    public bool TryGet(Guid id, out Episode? ep)
    {
        lock (_gate)
        {
            var ok = _lib.TryGetEpisode(id, out var found);
            ep = found;
            return ok;
        }
    }

    public Episode? Find(Guid id)
    {
        lock (_gate)
        {
            _lib.TryGetEpisode(id, out var found);
            return found;
        }
    }

    public IReadOnlyList<Episode> WhereByFeed(Guid feedId)
    {
        lock (_gate)
        {
            var result = new List<Episode>();
            foreach (var e in _lib.Current.Episodes)
                if (e.FeedId == feedId) result.Add(e);
            return result;
        }
    }

    public Episode AddOrUpdate(Episode ep)
    {
        if (ep == null) throw new ArgumentNullException(nameof(ep));

        Episode persisted;
        bool wasNew;
        lock (_gate)
        {
            wasNew = !_lib.TryGetEpisode(ep.Id, out _);
            persisted = _lib.AddOrUpdateEpisode(ep);
            _snapshot = null;
        }

        Fire(wasNew ? EpisodeChangeKind.Added : EpisodeChangeKind.Updated, persisted);
        return persisted;
    }

    public bool Remove(Guid id)
    {
        Episode? gone;
        lock (_gate)
        {
            if (!_lib.TryGetEpisode(id, out gone) || gone == null) return false;
            _lib.RemoveEpisode(id);
            _snapshot = null;
        }

        Fire(EpisodeChangeKind.Removed, gone);
        return true;
    }

    public int RemoveByFeed(Guid feedId)
    {
        List<Episode> removed;
        lock (_gate)
        {
            removed = _lib.Current.Episodes.Where(e => e.FeedId == feedId).ToList();
            if (removed.Count == 0) return 0;
            _lib.RemoveEpisodesByFeed(feedId);
            _snapshot = null;
        }

        foreach (var ep in removed) Fire(EpisodeChangeKind.Removed, ep);
        return removed.Count;
    }

    public void SetProgress(Guid id, long posMs, DateTimeOffset? lastPlayedAt)
    {
        Episode? target;
        lock (_gate)
        {
            if (!_lib.TryGetEpisode(id, out target) || target == null) return;
            _lib.SetEpisodeProgress(id, posMs, lastPlayedAt);
        }
        Fire(EpisodeChangeKind.Updated, target);
    }

    public void SetSaved(Guid id, bool saved)
    {
        Episode? target;
        lock (_gate)
        {
            if (!_lib.TryGetEpisode(id, out target) || target == null) return;
            _lib.SetSaved(id, saved);
        }
        Fire(EpisodeChangeKind.Updated, target);
    }

    public void SetManuallyMarkedPlayed(Guid id, bool played)
    {
        Episode? target;
        lock (_gate)
        {
            if (!_lib.TryGetEpisode(id, out target) || target == null) return;
            target.ManuallyMarkedPlayed = played;
            _lib.SaveAsync();
        }
        Fire(EpisodeChangeKind.Updated, target);
    }

    void Fire(EpisodeChangeKind kind, Episode ep)
    {
        try { Changed?.Invoke(new EpisodeChange(kind, ep)); } catch { /* subscriber errors swallowed */ }
    }
}
