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

        _data.Feeds.Should().ContainSingle(f => f.Url == "https://example.com/rss");
    }

    // ── RefreshFeedAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshFeedAsync_adds_new_episodes()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        _data.Episodes.Should().HaveCount(2);
        _data.Episodes.Select(e => e.AudioUrl).Should().Contain("https://example.com/ep1.mp3");
        _data.Episodes.Select(e => e.AudioUrl).Should().Contain("https://example.com/ep2.mp3");
    }

    [Fact]
    public async Task RefreshFeedAsync_deduplicates_by_audio_url()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);

        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        _handler.EnqueueXml(SampleRss); // second refresh with identical content
        await _feeds.RefreshFeedAsync(feed);

        _data.Episodes.Should().HaveCount(2, "refresh must not duplicate existing episodes");
    }

    [Fact]
    public async Task RefreshFeedAsync_preserves_user_flags_across_refresh()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        // User marks one episode as saved.
        var ep1 = _data.Episodes.First(e => e.AudioUrl == "https://example.com/ep1.mp3");
        ep1.Saved = true;

        _handler.EnqueueXml(SampleRss);
        await _feeds.RefreshFeedAsync(feed);

        _data.Episodes.First(e => e.AudioUrl == "https://example.com/ep1.mp3")
            .Saved.Should().BeTrue("refresh must not reset the Saved flag");
    }

    [Fact]
    public async Task RefreshFeedAsync_skips_items_without_audio()
    {
        _handler.EnqueueXml(RssNoAudio);
        _handler.EnqueueXml(RssNoAudio);

        await _feeds.AddFeedAsync("https://example.com/text");

        _data.Episodes.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshFeedAsync_parses_pubdate()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        await _feeds.AddFeedAsync("https://example.com/rss");

        _data.Episodes.Should().OnlyContain(e => e.PubDate.HasValue);
    }

    [Fact]
    public async Task RefreshFeedAsync_handles_malformed_xml_gracefully()
    {
        _handler.EnqueueXml("not valid xml");
        _handler.EnqueueXml("not valid xml");

        var feed = await _feeds.AddFeedAsync("https://example.com/bad");

        // Feed record exists but no episodes.
        _data.Feeds.Should().ContainSingle(f => f.Url == "https://example.com/bad");
        _data.Episodes.Should().BeEmpty();
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

        _data.Feeds.Should().BeEmpty();
        _data.Episodes.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveFeedAsync_removes_queue_entries_for_feed_episodes()
    {
        _handler.EnqueueXml(SampleRss);
        _handler.EnqueueXml(SampleRss);
        var feed = await _feeds.AddFeedAsync("https://example.com/rss");

        var ep = _data.Episodes.First();
        _data.Queue.Add(ep.Id);
        var otherEpId = Guid.NewGuid();
        _data.Queue.Add(otherEpId);

        await _feeds.RemoveFeedAsync(feed.Id);

        _data.Queue.Should().NotContain(ep.Id);
        _data.Queue.Should().Contain(otherEpId, "unrelated queue entries must stay");
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
                "mutations of _data.Feeds/Episodes must go through the dispatcher");
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
            _data.Feeds.Add(new Feed { Title = "X", Url = "https://x.com/rss" });

            await feeds2.RefreshAllAsync();

            dispatchCount.Should().BeGreaterThan(0,
                "RefreshAll must snapshot the feed list via dispatcher to avoid racing UI reads");
        }
        finally { feeds2.Dispose(); }
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
