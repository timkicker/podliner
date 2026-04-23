using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Storage;
using StuiPodcast.Infra.Tests.Fakes;
using Xunit;

namespace StuiPodcast.Infra.Tests.Feeds;

public sealed class FeedServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppData _data;
    private readonly AppFacade _app;
    private readonly FakeHttpHandler _handler;
    private readonly FeedService _feeds;

    public FeedServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-feedsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _data = new AppData();
        var config = new ConfigStore(_dir);
        var library = new LibraryStore(_dir);
        config.Load();
        library.Load();
        _app = new AppFacade(config, library);
        _handler = new FakeHttpHandler();
        _feeds = new FeedService(_data, _app, _handler);
    }

    public void Dispose()
    {
        try { _feeds.Dispose(); } catch { }
        try { _app.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Test Podcast</title>
            <link>https://example.com</link>
            <description>A test feed</description>
            <item>
              <title>Episode 1</title>
              <pubDate>Mon, 01 Jan 2024 10:00:00 +0000</pubDate>
              <description>First episode</description>
              <enclosure url="https://example.com/ep1.mp3" type="audio/mpeg" length="1000000"/>
            </item>
            <item>
              <title>Episode 2</title>
              <pubDate>Tue, 02 Jan 2024 10:00:00 +0000</pubDate>
              <description>Second episode</description>
              <enclosure url="https://example.com/ep2.mp3" type="audio/mpeg" length="2000000"/>
            </item>
          </channel>
        </rss>
        """;

    private const string RssNoAudio = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Text-only Feed</title>
            <link>https://example.com</link>
            <description>No audio</description>
            <item>
              <title>Text item</title>
              <description>Just text</description>
            </item>
          </channel>
        </rss>
        """;

    // ── AddFeedAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFeedAsync_parses_title_from_xml()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss); // refresh after add

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        feed.Title.Should().Be("Test Podcast");
        feed.Url.Should().Be("https://example.com/rss");
    }

    [Fact]
    public async Task AddFeedAsync_falls_back_to_url_when_title_empty()
    {
        _handler.EnqueueXml("<rss><channel><title></title><description>x</description></channel></rss>");
        _handler.EnqueueXml("<rss><channel><title></title><description>x</description></channel></rss>");

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        feed.Title.Should().Be("https://example.com/rss");
    }

    [Fact]
    public async Task AddFeedAsync_handles_http_error_gracefully()
    {
        _handler.EnqueueStatus(System.Net.HttpStatusCode.NotFound);
        _handler.EnqueueStatus(System.Net.HttpStatusCode.NotFound);

        var feed = await _feeds.AddFeedAsync("https://example.com/bad");

        // Still creates a placeholder feed record so the user sees their entry.
        feed.Should().NotBeNull();
        feed.Url.Should().Be("https://example.com/bad");
    }

    [Fact]
    public async Task AddFeedAsync_persists_feed_to_library()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);

        await _feeds.AddFeedAsync("https://example.com/rss");

        _app.Feeds.Should().ContainSingle(f => f.Url == "https://example.com/rss");
    }

    // ── RefreshFeedAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshFeedAsync_adds_new_episodes()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        _app.Episodes.Should().HaveCount(2);
        _app.Episodes.Select(e => e.AudioUrl).Should().Contain("https://example.com/ep1.mp3");
        _app.Episodes.Select(e => e.AudioUrl).Should().Contain("https://example.com/ep2.mp3");
    }

    [Fact]
    public async Task RefreshFeedAsync_deduplicates_by_audio_url()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        _handler.EnqueueXml(SampleRss); // second refresh with identical content
        await _feeds.RefreshFeedAsync(feed);

        _app.Episodes.Should().HaveCount(2, "refresh must not duplicate existing episodes");
    }

    [Fact]
    public async Task RefreshFeedAsync_preserves_user_flags_across_refresh()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        // User marks one episode as saved.
        var ep1 = _app.Episodes.First(e => e.AudioUrl == "https://example.com/ep1.mp3");
        ep1.Saved = true;

        _handler.EnqueueXml(SampleRss);
        await _feeds.RefreshFeedAsync(feed);

        _app.Episodes.First(e => e.AudioUrl == "https://example.com/ep1.mp3")
            .Saved.Should().BeTrue("refresh must not reset the Saved flag");
    }

    // ── guid-based dedup + CDN migration ─────────────────────────────────────

    private const string RssWithGuids = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Guid Feed</title>
            <item>
              <title>Episode A</title>
              <guid isPermaLink="false">stable-guid-aaa</guid>
              <enclosure url="https://old-cdn.example.com/a.mp3" type="audio/mpeg"/>
            </item>
          </channel>
        </rss>
        """;

    private const string RssWithGuidsMigrated = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Guid Feed</title>
            <item>
              <title>Episode A</title>
              <guid isPermaLink="false">stable-guid-aaa</guid>
              <enclosure url="https://new-cdn.example.com/a.mp3" type="audio/mpeg"/>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public async Task RefreshFeedAsync_persists_rss_guid_on_new_episodes()
    {
        _handler.EnqueueXml(RssWithGuids);
        _handler.EnqueueXml(RssWithGuids);

        await _feeds.AddFeedAsync("https://example.com/guidfeed");

        var ep = _app.Episodes.Single();
        ep.RssGuid.Should().Be("stable-guid-aaa");
    }

    [Fact]
    public async Task RefreshFeedAsync_follows_cdn_migration_via_guid()
    {
        // Initial fetch: old CDN URL.
        _handler.EnqueueXml(RssWithGuids);
        _handler.EnqueueXml(RssWithGuids);
        var feed = await _feeds.AddFeedAsync("https://example.com/guidfeed");

        _app.Episodes.Should().ContainSingle();
        var originalId = _app.Episodes.Single().Id;
        _app.Episodes.Single().AudioUrl.Should().Be("https://old-cdn.example.com/a.mp3");

        // Second refresh: publisher migrated CDN but kept the same guid.
        _handler.EnqueueXml(RssWithGuidsMigrated);
        await _feeds.RefreshFeedAsync(feed);

        _app.Episodes.Should().HaveCount(1, "guid match must update the URL in place, not insert a new episode");
        var updated = _app.Episodes.Single();
        updated.Id.Should().Be(originalId, "episode identity is preserved across CDN migrations");
        updated.AudioUrl.Should().Be("https://new-cdn.example.com/a.mp3", "AudioUrl must follow the feed");
        updated.RssGuid.Should().Be("stable-guid-aaa");
    }

    [Fact]
    public async Task RefreshFeedAsync_matches_by_title_and_pubdate_when_guid_and_url_miss()
    {
        // Brownfield case: library has an episode ingested before guid
        // persistence AND before the CDN migration (old URL, null guid).
        // The feed now advertises both a new URL and a new guid. Only
        // Title + PubDate still match.
        var feed = _app.AddOrUpdateFeed(new Feed { Title = "F", Url = "https://example.com/guidfeed" });
        _app.AddOrUpdateEpisode(new Episode
        {
            Id = Guid.NewGuid(),
            FeedId = feed.Id,
            Title = "Episode A",
            AudioUrl = "https://legacy-cdn.example.com/a.mp3",
            RssGuid = null,
            PubDate = DateTimeOffset.Parse("2024-01-15T10:00:00Z"),
            Progress = new Core.EpisodeProgress()
        });

        var rss = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>F</title>
                <item>
                  <title>Episode A</title>
                  <pubDate>Mon, 15 Jan 2024 10:00:00 +0000</pubDate>
                  <guid isPermaLink="false">stable-guid-aaa</guid>
                  <enclosure url="https://new-cdn.example.com/a.mp3" type="audio/mpeg"/>
                </item>
              </channel>
            </rss>
            """;
        _handler.EnqueueXml(rss);
        await _feeds.RefreshFeedAsync(feed);

        _app.Episodes.Should().HaveCount(1,
            "title+pubdate fallback must update the legacy entry in place, not insert a duplicate");
        var updated = _app.Episodes.Single();
        updated.AudioUrl.Should().Be("https://new-cdn.example.com/a.mp3");
        updated.RssGuid.Should().Be("stable-guid-aaa");
    }

    [Fact]
    public async Task RefreshFeedAsync_backfills_rss_guid_for_legacy_episodes()
    {
        // Seed an episode that looks like it was created before guid
        // persistence landed: matching URL, null RssGuid.
        var feed = _app.AddOrUpdateFeed(new Feed { Title = "F", Url = "https://example.com/guidfeed" });
        _app.AddOrUpdateEpisode(new Episode
        {
            Id = Guid.NewGuid(),
            FeedId = feed.Id,
            Title = "Episode A",
            AudioUrl = "https://old-cdn.example.com/a.mp3",
            RssGuid = null,
            Progress = new Core.EpisodeProgress()
        });

        _handler.EnqueueXml(RssWithGuids);
        await _feeds.RefreshFeedAsync(feed);

        _app.Episodes.Should().HaveCount(1);
        _app.Episodes.Single().RssGuid.Should().Be("stable-guid-aaa",
            "legacy episodes (null guid) matched via AudioUrl must get the guid backfilled");
    }

    [Fact]
    public async Task RefreshFeedAsync_skips_items_without_audio()
    {
        _handler.EnqueueXml(RssNoAudio);
        _handler.EnqueueXml(RssNoAudio);

        await _feeds.AddFeedAsync("https://example.com/text");

        _app.Episodes.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshFeedAsync_parses_pubdate()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        await _feeds.AddFeedAsync("https://example.com/rss");

        _app.Episodes.Should().OnlyContain(e => e.PubDate.HasValue);
    }

    [Fact]
    public async Task RefreshFeedAsync_handles_malformed_xml_gracefully()
    {
        _handler.EnqueueXml("not valid xml");
        _handler.EnqueueXml("not valid xml");

        var feed = await _feeds.AddFeedAsync("https://example.com/bad");

        // Feed record exists but no episodes.
        _app.Feeds.Should().ContainSingle(f => f.Url == "https://example.com/bad");
        _app.Episodes.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshFeedAsync_updates_LastChecked_on_success()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        var before = feed.LastChecked;
        await Task.Delay(10);
        _handler.EnqueueXml(SampleRss);
        await _feeds.RefreshFeedAsync(feed);

        feed.LastChecked.Should().BeAfter(before!.Value);
    }

    // ── RemoveFeedAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveFeedAsync_removes_feed_and_episodes()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        await _feeds.RemoveFeedAsync(feed.Id);

        _app.Feeds.Should().BeEmpty();
        _app.Episodes.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveFeedAsync_removes_queue_entries_for_feed_episodes()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        var ep = _app.Episodes.First();
        _app.LibraryStore.Current.Queue.Add(ep.Id);
        var otherEpId = Guid.NewGuid();
        _app.LibraryStore.Current.Queue.Add(otherEpId);

        await _feeds.RemoveFeedAsync(feed.Id);

        _app.Queue.Should().NotContain(ep.Id);
        _app.Queue.Should().Contain(otherEpId, "unrelated queue entries must stay");
    }

    [Fact]
    public async Task RemoveFeedAsync_clears_LastSelectedFeedId_if_matching()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");
        _data.LastSelectedFeedId = feed.Id;

        await _feeds.RemoveFeedAsync(feed.Id);

        _data.LastSelectedFeedId.Should().BeNull();
    }

    // ── RefreshAllAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAllAsync_iterates_all_feeds()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        await _feeds.AddFeedAsync("https://example.com/a");

        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        await _feeds.AddFeedAsync("https://example.com/b");

        var before = _handler.Requests.Count;

        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);

        await _feeds.RefreshAllAsync();

        _handler.Requests.Count.Should().BeGreaterThan(before, "RefreshAll must hit each feed");
    }

    [Fact]
    public async Task RefreshAllAsync_continues_after_single_feed_failure()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var a = await _feeds.AddFeedAsync("https://example.com/a");

        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var b = await _feeds.AddFeedAsync("https://example.com/b");

        // First refresh fails, second succeeds.
        _handler.EnqueueStatus(System.Net.HttpStatusCode.InternalServerError);
        _handler.EnqueueXml(SampleRss);

        var act = () => _feeds.RefreshAllAsync();
        await act.Should().NotThrowAsync();
    }

    // ── uiDispatch wiring ─────────────────────────────────────────────────────

    [Fact]
    public async Task AddFeedAsync_uses_uiDispatch_when_provided()
    {
        int dispatchCount = 0;
        Func<Action, Task> dispatcher = a => { dispatchCount++; a(); return Task.CompletedTask; };

        var feeds2 = new FeedService(_data, _app, _handler, dispatcher);
        try
        {
            _handler.EnqueueXml(SampleRss);
            _handler.EnqueueXml(SampleRss);
            await feeds2.AddFeedAsync("https://example.com/d");

            dispatchCount.Should().BeGreaterThan(0,
                "library mutations must go through the dispatcher");
        }
        finally { feeds2.Dispose(); }
    }

    [Fact]
    public async Task RefreshAllAsync_snapshots_feed_list_via_dispatcher()
    {
        int dispatchCount = 0;
        Func<Action, Task> dispatcher = a => { dispatchCount++; a(); return Task.CompletedTask; };

        var feeds2 = new FeedService(_data, _app, _handler, dispatcher);
        try
        {
            _app.AddOrUpdateFeed(new Feed { Title = "X", Url = "https://x.com/rss" });

            await feeds2.RefreshAllAsync();

            dispatchCount.Should().BeGreaterThan(0,
                "RefreshAll must snapshot the feed list via dispatcher to avoid racing UI reads");
        }
        finally { feeds2.Dispose(); }
    }

    // ── Conditional GET ──────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshFeedAsync_stores_etag_and_last_modified_on_200()
    {
        // AddFeedAsync does probe + refresh; freshness hints land via refresh.
        _handler.EnqueueXml(SampleRss); // probe
        _handler.Enqueue(_ =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SampleRss, System.Text.Encoding.UTF8, "application/rss+xml")
            };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");
            r.Content.Headers.LastModified = new DateTimeOffset(2024, 1, 5, 12, 0, 0, TimeSpan.Zero);
            return r;
        });

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        feed.LastEtag.Should().Be("\"abc123\"");
        feed.LastModified.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RefreshFeedAsync_sends_conditional_headers_next_time()
    {
        _handler.EnqueueXml(SampleRss); // probe
        _handler.Enqueue(_ =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SampleRss, System.Text.Encoding.UTF8, "application/rss+xml")
            };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return r;
        });

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");
        _handler.Requests.Clear();

        _handler.EnqueueStatus(System.Net.HttpStatusCode.NotModified);
        await _feeds.RefreshFeedAsync(feed);

        _handler.Requests.Should().HaveCount(1);
        _handler.Requests[0].Headers.TryGetValues("If-None-Match", out var ims).Should().BeTrue();
        ims!.Should().Contain("\"v1\"");
    }

    [Fact]
    public async Task RefreshFeedAsync_304_skips_parse_and_keeps_episodes_intact()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");
        var beforeCount = _app.Episodes.Count;
        var beforeLastChecked = feed.LastChecked;

        _handler.EnqueueStatus(System.Net.HttpStatusCode.NotModified);
        await _feeds.RefreshFeedAsync(feed);

        _app.Episodes.Should().HaveCount(beforeCount, "304 must not touch episode list");
        feed.LastChecked.Should().NotBe(beforeLastChecked, "LastChecked should still advance on 304");
    }

    [Fact]
    public async Task RefreshFeedAsync_304_keeps_existing_etag()
    {
        _handler.EnqueueXml(SampleRss); // probe
        _handler.Enqueue(_ =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SampleRss, System.Text.Encoding.UTF8, "application/rss+xml")
            };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"keep-me\"");
            return r;
        });

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        _handler.EnqueueStatus(System.Net.HttpStatusCode.NotModified);
        await _feeds.RefreshFeedAsync(feed);

        feed.LastEtag.Should().Be("\"keep-me\"");
    }

    // ── NewEpisodesDetected event (auto-download hook) ──────────────────────

    [Fact]
    public async Task RefreshFeedAsync_fires_NewEpisodesDetected_for_added_episodes()
    {
        _handler.EnqueueXml(SampleRss); // probe
        _handler.EnqueueXml(SampleRss); // refresh-after-add

        var received = new List<Guid>();
        _feeds.NewEpisodesDetected += (_, ids) => received.AddRange(ids);

        await _feeds.AddFeedAsync("https://example.com/rss");

        received.Should().HaveCount(2, "both sample items were new on first sync");
    }

    [Fact]
    public async Task RefreshFeedAsync_does_not_fire_when_nothing_new()
    {
        _handler.EnqueueXml(SampleRss); // probe
        _handler.EnqueueXml(SampleRss); // refresh-after-add
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        var received = new List<Guid>();
        _feeds.NewEpisodesDetected += (_, ids) => received.AddRange(ids);

        _handler.EnqueueXml(SampleRss); // identical content
        await _feeds.RefreshFeedAsync(feed);

        received.Should().BeEmpty();
    }

    // ── FeedRefreshFailed event + error categorization ──────────────────────

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound,          "404")]
    [InlineData(System.Net.HttpStatusCode.Forbidden,         "403")]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, "500")]
    public async Task RefreshFeedAsync_fires_failure_event_with_friendly_reason(
        System.Net.HttpStatusCode status, string expectedFragment)
    {
        _handler.EnqueueXml(SampleRss); // probe
        _handler.EnqueueXml(SampleRss); // refresh-after-add
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        string? captured = null;
        _feeds.FeedRefreshFailed += (_, reason) => captured = reason;

        _handler.EnqueueStatus(status);
        await _feeds.RefreshFeedAsync(feed);

        captured.Should().NotBeNull();
        captured.Should().Contain(expectedFragment);
    }

    [Fact]
    public async Task RefreshFeedAsync_fires_failure_event_on_transport_error()
    {
        _handler.EnqueueXml(SampleRss); // probe
        _handler.EnqueueXml(SampleRss); // refresh-after-add
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        string? captured = null;
        _feeds.FeedRefreshFailed += (_, reason) => captured = reason;

        _handler.EnqueueThrowing(new HttpRequestException("host unreachable"));
        await _feeds.RefreshFeedAsync(feed);

        captured.Should().NotBeNull();
        captured.Should().Contain("unreachable");
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_is_idempotent()
    {
        var feeds = new FeedService(_data, _app, new FakeHttpHandler());
        feeds.Dispose();
        var act = () => feeds.Dispose();
        act.Should().NotThrow();
    }
}
