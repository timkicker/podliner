using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Services;

// Thread-safe facade over LibraryStore for feed data. Mirrors EpisodeStore.
// Lookups by URL use a second index that we rebuild lazily when a mutation
// invalidates it; adding one feed doesn't force a full re-scan for the
// URL→Feed lookup of subsequent queries.
internal sealed class FeedStore : IFeedStore
{
    readonly LibraryStore _lib;
    readonly object _gate = new();
    IReadOnlyList<Feed>? _snapshot;
    Dictionary<string, Feed>? _byUrl;

    public event Action<FeedChange>? Changed;

    public FeedStore(LibraryStore lib)
    {
        _lib = lib ?? throw new ArgumentNullException(nameof(lib));
    }

    public int Count
    {
        get { lock (_gate) return _lib.Current.Feeds.Count; }
    }

    public IReadOnlyList<Feed> Snapshot()
    {
        lock (_gate) return _snapshot ??= _lib.Current.Feeds.ToArray();
    }

    public bool TryGet(Guid id, out Feed? feed)
    {
        lock (_gate)
        {
            foreach (var f in _lib.Current.Feeds)
            {
                if (f.Id == id) { feed = f; return true; }
            }
            feed = null;
            return false;
        }
    }

    public Feed? Find(Guid id)
    {
        TryGet(id, out var f);
        return f;
    }

    public Feed? FindByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (_gate)
        {
            EnsureUrlIndex_Locked();
            return _byUrl!.TryGetValue(url, out var f) ? f : null;
        }
    }

    public bool ContainsUrl(string url) => FindByUrl(url) != null;

    public Feed AddOrUpdate(Feed feed)
    {
        if (feed == null) throw new ArgumentNullException(nameof(feed));

        Feed persisted;
        bool wasNew;
        lock (_gate)
        {
            wasNew = feed.Id == Guid.Empty || !_lib.Current.Feeds.Any(f => f.Id == feed.Id);
            persisted = _lib.AddOrUpdateFeed(feed);
            _snapshot = null;
            _byUrl = null; // invalidate URL index
        }

        Fire(wasNew ? FeedChangeKind.Added : FeedChangeKind.Updated, persisted);
        return persisted;
    }

    public bool Remove(Guid id)
    {
        Feed? gone;
        lock (_gate)
        {
            gone = _lib.Current.Feeds.FirstOrDefault(f => f.Id == id);
            if (gone == null) return false;
            _lib.RemoveFeed(id);
            _snapshot = null;
            _byUrl = null;
        }

        Fire(FeedChangeKind.Removed, gone);
        return true;
    }

    void EnsureUrlIndex_Locked()
    {
        if (_byUrl != null) return;
        var dict = new Dictionary<string, Feed>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _lib.Current.Feeds)
            if (!string.IsNullOrEmpty(f.Url)) dict[f.Url] = f;
        _byUrl = dict;
    }

    void Fire(FeedChangeKind kind, Feed feed)
    {
        try { Changed?.Invoke(new FeedChange(kind, feed)); } catch { /* swallow subscriber errors */ }
    }
}
