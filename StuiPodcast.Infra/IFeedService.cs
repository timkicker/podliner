using StuiPodcast.Core;

namespace StuiPodcast.Infra;

// The feed lifecycle as the UI sees it. Extracted so the wiring that reacts
// to it (UiFeedWiring: add, remove, refresh, and the per-feed failure
// summary) can be tested without an AppFacade, a LibraryStore or a network.
//
// Four operations and two events is the whole surface; everything else in
// FeedService is sequencing and persistence the caller never touches.
public interface IFeedService : IDisposable
{
    // Fires after a refresh added episodes to a feed. Program.cs listens to
    // queue auto-downloads; listener exceptions are swallowed so one bad
    // subscriber cannot break a refresh pass.
    event Action<Feed, IReadOnlyList<Guid>>? NewEpisodesDetected;

    // Fires once per failed feed during a refresh, with a short reason fit
    // for an OSD ("HTTP 404", "timed out"). Subscribers are expected to
    // aggregate rather than show each one.
    event Action<Feed, string>? FeedRefreshFailed;

    Task<Feed> AddFeedAsync(string url);
    Task RemoveFeedAsync(Guid feedId, bool removeDownloads = false);
    Task RefreshAllAsync();
    Task RefreshFeedAsync(Feed feed);
}
