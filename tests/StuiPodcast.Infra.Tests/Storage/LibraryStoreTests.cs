using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.Infra.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly LibraryStore _store;

    public LibraryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-test-" + Guid.NewGuid().ToString("N"));
        _store = new LibraryStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_creates_empty_library_when_file_missing()
    {
        var lib = _store.Load();
        lib.Feeds.Should().BeEmpty();
        lib.Episodes.Should().BeEmpty();
        lib.Queue.Should().BeEmpty();
        lib.History.Should().BeEmpty();
    }

    [Fact]
    public void SaveNow_then_Load_roundtrips_data()
    {
        _store.Load();

        var feed = new Feed { Title = "Test Feed", Url = "https://example.com/feed.xml" };
        _store.AddOrUpdateFeed(feed);

        var ep = new Episode
        {
            FeedId = feed.Id,
            Title = "Episode 1",
            AudioUrl = "https://example.com/ep1.mp3",
            DurationMs = 60000
        };
        _store.AddOrUpdateEpisode(ep);
        _store.SaveNow();

        // Load into fresh store
        var store2 = new LibraryStore(_dir);
        var lib = store2.Load();
        lib.Feeds.Should().HaveCount(1);
        lib.Feeds[0].Title.Should().Be("Test Feed");
        lib.Episodes.Should().HaveCount(1);
        lib.Episodes[0].Title.Should().Be("Episode 1");
    }

    [Fact]
    public void AddOrUpdateFeed_upserts_existing()
    {
        _store.Load();
        var feed = new Feed { Title = "Original", Url = "https://example.com/feed.xml" };
        _store.AddOrUpdateFeed(feed);

        feed.Title = "Updated";
        _store.AddOrUpdateFeed(feed);

        _store.Current.Feeds.Should().HaveCount(1);
        _store.Current.Feeds[0].Title.Should().Be("Updated");
    }

    [Fact]
    public void AddOrUpdateEpisode_upserts_existing_preserves_usage_flags()
    {
        _store.Load();
        var feed = new Feed { Title = "Feed", Url = "https://example.com/f.xml" };
        _store.AddOrUpdateFeed(feed);

        var ep = new Episode { FeedId = feed.Id, Title = "Ep", AudioUrl = "https://example.com/e.mp3" };
        _store.AddOrUpdateEpisode(ep);

        // Set usage flags
        _store.SetSaved(ep.Id, true);
        _store.SetEpisodeProgress(ep.Id, 5000, DateTimeOffset.UtcNow);

        // Update metadata
        var updated = new Episode { Id = ep.Id, FeedId = feed.Id, Title = "Ep Updated", AudioUrl = "https://example.com/e2.mp3" };
        _store.AddOrUpdateEpisode(updated);

        // Metadata updated, usage preserved
        var result = _store.Current.Episodes[0];
        result.Title.Should().Be("Ep Updated");
        result.Saved.Should().BeTrue();
        result.Progress.LastPosMs.Should().Be(5000);
    }

    [Fact]
    public void AddOrUpdateEpisode_rejects_orphan_episode()
    {
        _store.Load();
        var ep = new Episode { FeedId = Guid.NewGuid(), Title = "Orphan", AudioUrl = "https://x.com/a.mp3" };
        var act = () => _store.AddOrUpdateEpisode(ep);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetEpisodeProgress_clamps_position_to_duration()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        var ep = new Episode { FeedId = feed.Id, Title = "E", AudioUrl = "https://x.com/e.mp3", DurationMs = 10000 };
        _store.AddOrUpdateEpisode(ep);

        _store.SetEpisodeProgress(ep.Id, 99999, null);
        _store.Current.Episodes[0].Progress.LastPosMs.Should().Be(10000);
    }

    [Fact]
    public void SetEpisodeProgress_clamps_negative_to_zero()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        var ep = new Episode { FeedId = feed.Id, Title = "E", AudioUrl = "https://x.com/e.mp3", DurationMs = 10000 };
        _store.AddOrUpdateEpisode(ep);

        _store.SetEpisodeProgress(ep.Id, -500, null);
        _store.Current.Episodes[0].Progress.LastPosMs.Should().Be(0);
    }

    [Fact]
    public void QueuePush_and_QueueRemove()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        var ep = new Episode { FeedId = feed.Id, Title = "E", AudioUrl = "https://x.com/e.mp3" };
        _store.AddOrUpdateEpisode(ep);

        _store.QueuePush(ep.Id);
        _store.Current.Queue.Should().Contain(ep.Id);

        _store.QueueRemove(ep.Id).Should().BeTrue();
        _store.Current.Queue.Should().NotContain(ep.Id);
    }

    [Fact]
    public void QueueTrimBefore_removes_entries_up_to_and_including_id()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);

        var ep1 = new Episode { FeedId = feed.Id, Title = "E1", AudioUrl = "https://x.com/1.mp3" };
        var ep2 = new Episode { FeedId = feed.Id, Title = "E2", AudioUrl = "https://x.com/2.mp3" };
        var ep3 = new Episode { FeedId = feed.Id, Title = "E3", AudioUrl = "https://x.com/3.mp3" };
        _store.AddOrUpdateEpisode(ep1);
        _store.AddOrUpdateEpisode(ep2);
        _store.AddOrUpdateEpisode(ep3);

        _store.QueuePush(ep1.Id);
        _store.QueuePush(ep2.Id);
        _store.QueuePush(ep3.Id);

        _store.QueueTrimBefore(ep2.Id);
        _store.Current.Queue.Should().Equal(ep3.Id);
    }

    [Fact]
    public void RemoveFeed_removes_feed_from_list()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);

        _store.RemoveFeed(feed.Id).Should().BeTrue();
        _store.Current.Feeds.Should().BeEmpty();
    }

    [Fact]
    public void RemoveEpisodesByFeed_removes_all_episodes_of_feed()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        _store.AddOrUpdateEpisode(new Episode { FeedId = feed.Id, Title = "E1", AudioUrl = "https://x.com/1.mp3" });
        _store.AddOrUpdateEpisode(new Episode { FeedId = feed.Id, Title = "E2", AudioUrl = "https://x.com/2.mp3" });

        _store.RemoveEpisodesByFeed(feed.Id).Should().Be(2);
        _store.Current.Episodes.Should().BeEmpty();
    }

    [Fact]
    public void Load_handles_corrupt_json_gracefully()
    {
        _store.Load();
        _store.SaveNow();

        // Corrupt the file
        File.WriteAllText(_store.FilePath, "{ this is not valid json }}}");

        var store2 = new LibraryStore(_dir);
        var lib = store2.Load();
        lib.Feeds.Should().BeEmpty(); // defaults
    }

    [Fact]
    public void Load_validates_and_removes_orphan_episodes()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        var ep = new Episode { FeedId = feed.Id, Title = "E", AudioUrl = "https://x.com/e.mp3" };
        _store.AddOrUpdateEpisode(ep);
        _store.SaveNow();

        // Remove feed, leave orphan episode in JSON
        var store2 = new LibraryStore(_dir);
        var lib = store2.Load();
        store2.RemoveFeed(feed.Id);
        store2.SaveNow();

        // Reload — episode should be cleaned up as orphan
        var store3 = new LibraryStore(_dir);
        var lib3 = store3.Load();
        lib3.Episodes.Should().BeEmpty();
    }

    [Fact]
    public void Load_normalizes_negative_duration_and_progress()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        var ep = new Episode
        {
            FeedId = feed.Id, Title = "E", AudioUrl = "https://x.com/e.mp3",
            DurationMs = -100,
            Progress = new EpisodeProgress { LastPosMs = -50 }
        };
        _store.AddOrUpdateEpisode(ep);

        // The store should have normalized on add
        _store.Current.Episodes[0].DurationMs.Should().Be(0);
        _store.Current.Episodes[0].Progress.LastPosMs.Should().Be(0);
    }

    [Fact]
    public void HistoryAdd_and_HistoryClear()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        var ep = new Episode { FeedId = feed.Id, Title = "E", AudioUrl = "https://x.com/e.mp3" };
        _store.AddOrUpdateEpisode(ep);

        var now = DateTimeOffset.UtcNow;
        _store.HistoryAdd(ep.Id, now);
        _store.Current.History.Should().HaveCount(1);
        _store.Current.History[0].EpisodeId.Should().Be(ep.Id);

        _store.HistoryClear();
        _store.Current.History.Should().BeEmpty();
    }

    [Fact]
    public void QueueRemoveByEpisodeIds_removes_matching()
    {
        _store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        _store.AddOrUpdateFeed(feed);
        var ep1 = new Episode { FeedId = feed.Id, Title = "E1", AudioUrl = "https://x.com/1.mp3" };
        var ep2 = new Episode { FeedId = feed.Id, Title = "E2", AudioUrl = "https://x.com/2.mp3" };
        var ep3 = new Episode { FeedId = feed.Id, Title = "E3", AudioUrl = "https://x.com/3.mp3" };
        _store.AddOrUpdateEpisode(ep1);
        _store.AddOrUpdateEpisode(ep2);
        _store.AddOrUpdateEpisode(ep3);
        _store.QueuePush(ep1.Id);
        _store.QueuePush(ep2.Id);
        _store.QueuePush(ep3.Id);

        _store.QueueRemoveByEpisodeIds(new[] { ep1.Id, ep3.Id }).Should().Be(2);
        _store.Current.Queue.Should().Equal(ep2.Id);
    }

    [Fact]
    public void Orphaned_tmp_file_is_cleaned_on_load()
    {
        // Create the directory and a fake tmp file
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        File.WriteAllText(_store.TmpPath, "stale");

        _store.Load();
        File.Exists(_store.TmpPath).Should().BeFalse();
    }
}
