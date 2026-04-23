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

    // ── Title+PubDate dedup on load ──────────────────────────────────────────
    // Brownfield heal: libraries that accumulated duplicates during a CDN
    // migration (pre-guid vs post-guid refresh runs) must be collapsed on
    // the next Load so the user sees a clean list without having to run
    // :refresh.

    [Fact]
    public void Load_merges_title_pubdate_duplicates_preferring_user_state()
    {
        var pub = DateTimeOffset.Parse("2024-02-27T12:00:00Z");

        // Hand-craft a library.json with two rows for the same episode:
        // legacy (svmaudio URL, null guid, with user progress) + new
        // (audiorella URL, guid set, no user state).
        var legacyId = Guid.NewGuid();
        var freshId  = Guid.NewGuid();
        var feedId   = Guid.NewGuid();

        var json = $$"""
        {
          "SchemaVersion": 1,
          "Feeds": [
            { "Id": "{{feedId}}", "Title": "VdV", "Url": "https://example.com/feed" }
          ],
          "Episodes": [
            {
              "Id": "{{legacyId}}",
              "FeedId": "{{feedId}}",
              "Title": "Episode A",
              "AudioUrl": "https://legacy-cdn.example.com/a.mp3",
              "RssGuid": null,
              "PubDate": "2024-02-27T12:00:00+00:00",
              "Saved": true,
              "Progress": { "LastPosMs": 45000, "LastPlayedAt": "2024-03-01T10:00:00+00:00" }
            },
            {
              "Id": "{{freshId}}",
              "FeedId": "{{feedId}}",
              "Title": "Episode A",
              "AudioUrl": "https://new-cdn.example.com/a.mp3",
              "RssGuid": "stable-guid-aaa",
              "PubDate": "2024-02-27T12:00:00+00:00",
              "Saved": false,
              "Progress": { "LastPosMs": 0 }
            }
          ],
          "Queue": ["{{legacyId}}"],
          "History": []
        }
        """;
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        File.WriteAllText(Path.Combine(_dir, "library", "library.json"), json);

        var lib = _store.Load();

        lib.Episodes.Should().ContainSingle("duplicates must be collapsed on load");
        var winner = lib.Episodes.Single();

        // Winner keeps legacy Id (it was the one with user state, so
        // queue/history references stay valid) ...
        winner.Id.Should().Be(legacyId);
        winner.Saved.Should().BeTrue();
        winner.Progress.LastPosMs.Should().Be(45000);

        // ... but adopts the freshest CDN URL + guid so playback works.
        winner.AudioUrl.Should().Be("https://new-cdn.example.com/a.mp3");
        winner.RssGuid.Should().Be("stable-guid-aaa");

        // Queue entry is preserved (still points to winner.Id).
        lib.Queue.Should().ContainSingle().Which.Should().Be(legacyId);
    }

    [Fact]
    public void Load_rewrites_queue_refs_to_winner_when_loser_was_queued()
    {
        // Edge case: it's the *loser* (no user state) that's in the queue,
        // e.g. user queued the audiorella entry then never played it, while
        // the svmaudio entry holds older progress. The rewrite must keep
        // the queue intact by redirecting loser.Id → winner.Id.
        var pub = DateTimeOffset.Parse("2024-02-27T12:00:00Z");
        var legacyId = Guid.NewGuid();
        var freshId  = Guid.NewGuid();
        var feedId   = Guid.NewGuid();

        var json = $$"""
        {
          "SchemaVersion": 1,
          "Feeds": [
            { "Id": "{{feedId}}", "Title": "F", "Url": "https://example.com/feed" }
          ],
          "Episodes": [
            {
              "Id": "{{legacyId}}",
              "FeedId": "{{feedId}}",
              "Title": "Episode A",
              "AudioUrl": "https://legacy-cdn.example.com/a.mp3",
              "RssGuid": null,
              "PubDate": "2024-02-27T12:00:00+00:00",
              "Saved": true,
              "Progress": { "LastPosMs": 30000 }
            },
            {
              "Id": "{{freshId}}",
              "FeedId": "{{feedId}}",
              "Title": "Episode A",
              "AudioUrl": "https://new-cdn.example.com/a.mp3",
              "RssGuid": "g1",
              "PubDate": "2024-02-27T12:00:00+00:00",
              "Progress": { "LastPosMs": 0 }
            }
          ],
          "Queue": ["{{freshId}}"],
          "History": [ { "EpisodeId": "{{freshId}}", "At": "2024-03-01T10:00:00+00:00" } ]
        }
        """;
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        File.WriteAllText(Path.Combine(_dir, "library", "library.json"), json);

        var lib = _store.Load();

        lib.Episodes.Should().ContainSingle();
        var winner = lib.Episodes.Single();
        winner.Id.Should().Be(legacyId, "winner is picked for user state");

        lib.Queue.Should().ContainSingle().Which.Should().Be(legacyId,
            "queue entry that pointed to the loser must be redirected to the winner");
        lib.History.Should().ContainSingle().Which.EpisodeId.Should().Be(legacyId,
            "history entry must be redirected too");
    }

    [Fact]
    public void Load_merges_duplicates_with_trailing_whitespace_in_title()
    {
        // Real-world brownfield: the RSS parser that ingested the legacy
        // entries preserved a trailing space in the title, the post-guid
        // refresh trimmed it. Naive Title equality would keep them apart,
        // Title.Trim() collapses them.
        var feedId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        var freshId  = Guid.NewGuid();

        var json = $$"""
        {
          "SchemaVersion": 1,
          "Feeds": [
            { "Id": "{{feedId}}", "Title": "F", "Url": "https://example.com/feed" }
          ],
          "Episodes": [
            {
              "Id": "{{legacyId}}",
              "FeedId": "{{feedId}}",
              "Title": "Episode A ",
              "AudioUrl": "https://legacy-cdn.example.com/a.mp3",
              "RssGuid": null,
              "PubDate": "2024-01-01T00:00:00+00:00",
              "Progress": { "LastPosMs": 0 }
            },
            {
              "Id": "{{freshId}}",
              "FeedId": "{{feedId}}",
              "Title": "Episode A",
              "AudioUrl": "https://new-cdn.example.com/a.mp3",
              "RssGuid": "g1",
              "PubDate": "2024-01-01T00:00:00+00:00",
              "Progress": { "LastPosMs": 0 }
            }
          ],
          "Queue": [],
          "History": []
        }
        """;
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        File.WriteAllText(Path.Combine(_dir, "library", "library.json"), json);

        var lib = _store.Load();

        lib.Episodes.Should().ContainSingle("trailing whitespace must not defeat dedup");
        lib.Episodes.Single().AudioUrl.Should().Be("https://new-cdn.example.com/a.mp3");
        lib.Episodes.Single().RssGuid.Should().Be("g1");
    }

    [Fact]
    public void Load_keeps_distinct_episodes_with_same_title_but_different_pubdate()
    {
        // Regression guard: re-uploads and recap episodes legitimately
        // share a title. They must survive as separate rows as long as
        // PubDate differs.
        var feedId = Guid.NewGuid();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var json = $$"""
        {
          "SchemaVersion": 1,
          "Feeds": [
            { "Id": "{{feedId}}", "Title": "F", "Url": "https://example.com/feed" }
          ],
          "Episodes": [
            { "Id": "{{id1}}", "FeedId": "{{feedId}}", "Title": "Weekly Recap", "AudioUrl": "https://a.mp3", "PubDate": "2024-01-07T00:00:00+00:00", "Progress": { "LastPosMs": 0 } },
            { "Id": "{{id2}}", "FeedId": "{{feedId}}", "Title": "Weekly Recap", "AudioUrl": "https://b.mp3", "PubDate": "2024-01-14T00:00:00+00:00", "Progress": { "LastPosMs": 0 } }
          ],
          "Queue": [],
          "History": []
        }
        """;
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        File.WriteAllText(Path.Combine(_dir, "library", "library.json"), json);

        var lib = _store.Load();
        lib.Episodes.Should().HaveCount(2);
    }

    // ── URL-based feed dedup (brownfield heal + AddOrUpdateFeed guard) ──────

    [Fact]
    public void AddOrUpdateFeed_dedups_by_url_when_called_with_fresh_guid()
    {
        _store.Load();
        var first  = new Feed { Title = "Darknet", Url = "https://feeds.example.com/d" };
        var second = new Feed { Title = "Darknet Diaries", Url = "https://feeds.example.com/d" };

        var a = _store.AddOrUpdateFeed(first);
        var b = _store.AddOrUpdateFeed(second);

        b.Id.Should().Be(a.Id, "identical URL should return the existing row");
        b.Title.Should().Be("Darknet Diaries", "new title should overwrite placeholder");
        _store.Current.Feeds.Should().ContainSingle();
    }

    [Fact]
    public void Load_drops_feeds_with_non_http_urls()
    {
        var feedId = Guid.NewGuid();
        var badId  = Guid.NewGuid();
        var json = $$"""
        {
          "SchemaVersion": 1,
          "Feeds": [
            { "Id": "{{feedId}}", "Title": "Real",  "Url": "https://example.com/feed" },
            { "Id": "{{badId}}",  "Title": "<feed>", "Url": "<feed>" }
          ],
          "Episodes": [],
          "Queue": [],
          "History": []
        }
        """;
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        File.WriteAllText(Path.Combine(_dir, "library", "library.json"), json);

        var lib = _store.Load();
        lib.Feeds.Should().ContainSingle().Which.Title.Should().Be("Real");
    }

    [Fact]
    public void Load_collapses_url_duplicates_and_rewrites_episode_feed_ids()
    {
        var winnerId = Guid.NewGuid();
        var loserId  = Guid.NewGuid();
        var ep1Id    = Guid.NewGuid();
        var ep2Id    = Guid.NewGuid();
        var json = $$"""
        {
          "SchemaVersion": 1,
          "Feeds": [
            { "Id": "{{winnerId}}", "Title": "A", "Url": "https://example.com/d" },
            { "Id": "{{loserId}}",  "Title": "A", "Url": "https://example.com/d" }
          ],
          "Episodes": [
            { "Id": "{{ep1Id}}", "FeedId": "{{winnerId}}", "Title": "Ep1", "AudioUrl": "https://a/1.mp3", "Progress": { "LastPosMs": 0 } },
            { "Id": "{{ep2Id}}", "FeedId": "{{loserId}}",  "Title": "Ep2", "AudioUrl": "https://a/2.mp3", "Progress": { "LastPosMs": 0 } }
          ],
          "Queue": [],
          "History": []
        }
        """;
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        File.WriteAllText(Path.Combine(_dir, "library", "library.json"), json);

        var lib = _store.Load();

        lib.Feeds.Should().ContainSingle().Which.Id.Should().Be(winnerId);
        lib.Episodes.Should().HaveCount(2);
        lib.Episodes.Should().OnlyContain(e => e.FeedId == winnerId,
            "loser-linked episodes must be rewritten to the winning feed id");
    }
}
