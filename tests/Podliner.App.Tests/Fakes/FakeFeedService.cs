using Podliner.Core;
using Podliner.Infra;

namespace Podliner.App.Tests.Fakes;

// Stands in for FeedService: records what was asked of it, lets a test
// script the outcome, and can raise the two events the wiring listens to.
sealed class FakeFeedService : IFeedService
{
    public event Action<Feed, IReadOnlyList<Guid>>? NewEpisodesDetected;
    public event Action<Feed, string>? FeedRefreshFailed;

    public readonly List<string> AddedUrls = new();
    public readonly List<(Guid FeedId, bool RemoveDownloads)> Removals = new();
    public int RefreshAllCalls { get; private set; }
    public readonly List<Feed> RefreshedFeeds = new();
    public bool Disposed { get; private set; }

    // Set to make the next call of the matching kind throw.
    public Exception? ThrowOnAdd { get; set; }
    public Exception? ThrowOnRemove { get; set; }
    public Exception? ThrowOnRefreshAll { get; set; }

    // Raised during RefreshAllAsync, in order, to simulate failing feeds.
    public readonly List<(Feed Feed, string Reason)> FailuresToRaise = new();

    public Feed AddResult { get; set; } = new() { Id = Guid.NewGuid(), Title = "Added", Url = "https://ex.test/f.xml" };

    public Task<Feed> AddFeedAsync(string url)
    {
        AddedUrls.Add(url);
        if (ThrowOnAdd is { } ex) throw ex;
        return Task.FromResult(AddResult);
    }

    public Task RemoveFeedAsync(Guid feedId, bool removeDownloads = false)
    {
        Removals.Add((feedId, removeDownloads));
        if (ThrowOnRemove is { } ex) throw ex;
        return Task.CompletedTask;
    }

    public Task RefreshAllAsync()
    {
        RefreshAllCalls++;
        if (ThrowOnRefreshAll is { } ex) throw ex;
        foreach (var (feed, reason) in FailuresToRaise)
            FeedRefreshFailed?.Invoke(feed, reason);
        return Task.CompletedTask;
    }

    public Task RefreshFeedAsync(Feed feed)
    {
        RefreshedFeeds.Add(feed);
        return Task.CompletedTask;
    }

    public void RaiseNewEpisodes(Feed feed, params Guid[] ids)
        => NewEpisodesDetected?.Invoke(feed, ids);

    public void Dispose() => Disposed = true;
}
