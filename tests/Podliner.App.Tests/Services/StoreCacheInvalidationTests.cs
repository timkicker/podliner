using FluentAssertions;
using Podliner.App.Services;
using Podliner.Core;
using Podliner.Infra.Storage;
using Xunit;

namespace Podliner.App.Tests.Services;

// FeedStore and EpisodeStore cache a snapshot of LibraryStore.Current and drop
// it on their own mutations. But FeedService lives in Infra and writes through
// AppFacade straight into LibraryStore, which the App-side stores cannot see.
//
// That is the whole ":add <url>" bug: the feed is fetched, persisted and
// logged as added, then the sidebar paints a cached list that predates it and
// stays empty until the next start.
public sealed class StoreCacheInvalidationTests : IDisposable
{
    private readonly string _dir;
    private readonly LibraryStore _lib;

    public StoreCacheInvalidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-storecache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lib = new LibraryStore(_dir);
        _lib.Load();
    }

    public void Dispose()
    {
        try { _lib.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_feed_added_straight_to_the_library_shows_up_in_the_store()
    {
        var store = new FeedStore(_lib);
        store.Snapshot().Should().BeEmpty();

        // What FeedService.AddFeedAsync does, via AppFacade.
        _lib.AddOrUpdateFeed(new Feed { Title = "Darknet Diaries", Url = "https://example.test/rss" });

        store.Snapshot().Should().ContainSingle().Which.Title.Should().Be("Darknet Diaries");
    }

    [Fact]
    public void Episodes_added_by_a_refresh_show_up_in_the_store()
    {
        var store = new EpisodeStore(_lib);
        // Episodes need a feed to hang off, same as after a real ":add".
        var feedId = _lib.AddOrUpdateFeed(new Feed { Title = "F", Url = "https://example.test/f" }).Id;
        store.Snapshot().Should().BeEmpty();

        // What FeedService.RefreshFeedAsync does for every new item.
        for (int i = 0; i < 3; i++)
            _lib.AddOrUpdateEpisode(new Episode
            {
                Id = Guid.NewGuid(), FeedId = feedId,
                Title = $"ep-{i}", AudioUrl = $"https://example.test/{i}.mp3"
            });

        store.Snapshot().Should().HaveCount(3);
    }

    [Fact]
    public void A_feed_removed_straight_from_the_library_disappears_from_the_store()
    {
        var store = new FeedStore(_lib);
        var feed = _lib.AddOrUpdateFeed(new Feed { Title = "Gone", Url = "https://example.test/gone" });
        store.Snapshot().Should().ContainSingle();

        _lib.RemoveFeed(feed.Id);

        store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void The_url_index_notices_a_feed_added_behind_the_stores_back()
    {
        var store = new FeedStore(_lib);
        store.ContainsUrl("https://example.test/rss").Should().BeFalse();

        _lib.AddOrUpdateFeed(new Feed { Title = "Later", Url = "https://example.test/rss" });

        store.ContainsUrl("https://example.test/rss").Should().BeTrue(
            "otherwise ':add' on an existing feed stops reporting 'already added'");
    }

    [Fact]
    public void Repeated_snapshots_with_no_mutation_hand_back_the_same_instance()
    {
        var store = new FeedStore(_lib);
        _lib.AddOrUpdateFeed(new Feed { Title = "Stable", Url = "https://example.test/s" });

        var first = store.Snapshot();
        var second = store.Snapshot();

        second.Should().BeSameAs(first, "the cache is there to keep the hot render path off a full copy");
    }
}
