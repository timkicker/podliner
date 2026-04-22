using StuiPodcast.App.Services;
using StuiPodcast.Core;

namespace StuiPodcast.App.Tests.Fakes;

sealed class FakeFeedStore : IFeedStore
{
    private readonly Dictionary<Guid, Feed> _byId = new();
    private readonly List<Feed> _ordered = new();

    public event Action<FeedChange>? Changed;

    public int Count => _ordered.Count;
    public IReadOnlyList<Feed> Snapshot() => _ordered.ToArray();

    public bool TryGet(Guid id, out Feed? feed)
    {
        var ok = _byId.TryGetValue(id, out var f);
        feed = f;
        return ok;
    }

    public Feed? Find(Guid id) => _byId.TryGetValue(id, out var f) ? f : null;

    public Feed? FindByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return _ordered.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
    }

    public bool ContainsUrl(string url) => FindByUrl(url) != null;

    public Feed AddOrUpdate(Feed feed)
    {
        if (feed.Id == Guid.Empty) feed.Id = Guid.NewGuid();
        bool isNew = !_byId.ContainsKey(feed.Id);
        _byId[feed.Id] = feed;
        if (isNew) _ordered.Add(feed);
        Changed?.Invoke(new FeedChange(isNew ? FeedChangeKind.Added : FeedChangeKind.Updated, feed));
        return feed;
    }

    public bool Remove(Guid id)
    {
        if (!_byId.TryGetValue(id, out var feed)) return false;
        _byId.Remove(id);
        _ordered.RemoveAll(f => f.Id == id);
        Changed?.Invoke(new FeedChange(FeedChangeKind.Removed, feed));
        return true;
    }

    public void Seed(params Feed[] feeds)
    {
        foreach (var f in feeds)
        {
            if (f.Id == Guid.Empty) f.Id = Guid.NewGuid();
            _byId[f.Id] = f;
            _ordered.Add(f);
        }
    }
}
