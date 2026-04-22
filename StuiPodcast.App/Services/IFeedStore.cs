using StuiPodcast.Core;

namespace StuiPodcast.App.Services;

// Single source of truth for feeds at runtime. Mirrors IEpisodeStore in
// shape and guarantees (thread-safe, snapshot-cached, event-raising).
// Motivation (same as episodes): the dual list between AppData.Feeds and
// LibraryStore.Current.Feeds allowed races and drift; O(n) linear scans
// for Id/Url lookups added up on refresh/sync paths with many feeds.
public interface IFeedStore
{
    int Count { get; }
    IReadOnlyList<Feed> Snapshot();
    bool TryGet(Guid id, out Feed? feed);
    Feed? Find(Guid id);
    Feed? FindByUrl(string url);
    bool ContainsUrl(string url);

    // Upsert: add or update metadata (Title/Url/LastChecked). Preserves Id.
    Feed AddOrUpdate(Feed feed);
    bool Remove(Guid id);

    event Action<FeedChange>? Changed;
}

public enum FeedChangeKind { Added, Updated, Removed }

public readonly record struct FeedChange(FeedChangeKind Kind, Feed Feed);
