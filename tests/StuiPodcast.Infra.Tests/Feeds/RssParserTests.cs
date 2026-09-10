using CodeHollow.FeedReader;
using FluentAssertions;
using StuiPodcast.Infra.Feeds;
using Xunit;

namespace StuiPodcast.Infra.Tests.Feeds;

public sealed class RssParserTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static FeedItem FirstItem(string itemXml, string extraNs = "")
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"
                 xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"
                 xmlns:media="http://search.yahoo.com/mrss/"
                 xmlns:podcast="https://podcastindex.org/namespace/1.0"{extraNs}>
              <channel>
                <title>Test Feed</title>
                <item>{itemXml}</item>
              </channel>
            </rss>
            """;
        return FeedReader.ReadFromString(xml).Items.Single();
    }

    // ── HtmlToText ──────────────────────────────────────────────────────────
    // Covers the AngleSharp dependency. Shownotes come straight from
    // publishers, so this runs over arbitrary third-party HTML.

    [Fact]
    public void HtmlToText_strips_nested_tags()
    {
        RssParser.HtmlToText("<p>Hello <b><i>there</i></b> world</p>")
            .Should().Be("Hello there world");
    }

    [Fact]
    public void HtmlToText_decodes_entities()
    {
        RssParser.HtmlToText("<p>Fish &amp; Chips &lt;tag&gt; &quot;quoted&quot; caf&eacute;</p>")
            .Should().Be("Fish & Chips <tag> \"quoted\" café");
    }

    [Fact]
    public void HtmlToText_drops_script_and_style_content()
    {
        var text = RssParser.HtmlToText(
            "<style>body{color:red}</style><p>visible</p><script>alert('x')</script>");

        text.Should().Contain("visible");
        text.Should().NotContain("alert");
        text.Should().NotContain("color:red");
    }

    [Fact]
    public void HtmlToText_keeps_plain_text_unchanged()
    {
        RssParser.HtmlToText("just plain text").Should().Be("just plain text");
    }

    [Fact]
    public void HtmlToText_trims_surrounding_whitespace()
    {
        RssParser.HtmlToText("   <p>  padded  </p>   ").Should().Be("padded");
    }

    [Fact]
    public void HtmlToText_handles_empty_and_null()
    {
        RssParser.HtmlToText("").Should().Be("");
        RssParser.HtmlToText(null!).Should().Be("");
    }

    [Fact]
    public void HtmlToText_survives_unclosed_tags()
    {
        RssParser.HtmlToText("<p>one<p>two<div>three")
            .Should().Contain("one").And.Contain("two").And.Contain("three");
    }

    // ── ParseDurationToMs ───────────────────────────────────────────────────

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1234", 1_234_000L)]
    [InlineData("90", 90_000L)]
    public void ParseDurationToMs_reads_bare_seconds(string input, long expected)
        => RssParser.ParseDurationToMs(input).Should().Be(expected);

    [Theory]
    [InlineData("05:30", 330_000L)]
    [InlineData("1:00:00", 3_600_000L)]
    [InlineData("01:23:45", 5_025_000L)]
    [InlineData("00:00:01", 1_000L)]
    public void ParseDurationToMs_reads_clock_format(string input, long expected)
        => RssParser.ParseDurationToMs(input).Should().Be(expected);

    [Theory]
    [InlineData("PT1H23M45S", 5_025_000L)]
    [InlineData("PT30M", 1_800_000L)]
    public void ParseDurationToMs_reads_iso8601(string input, long expected)
        => RssParser.ParseDurationToMs(input).Should().Be(expected);

    [Fact]
    public void ParseDurationToMs_trims_whitespace()
        => RssParser.ParseDurationToMs("  05:30  ").Should().Be(330_000L);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("-5")]
    [InlineData("aa:bb")]
    [InlineData("1:2:3:4")]
    public void ParseDurationToMs_returns_null_on_garbage(string? input)
        => RssParser.ParseDurationToMs(input).Should().BeNull();

    // ── IsAudioUrl ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://x.test/a.mp3")]
    [InlineData("https://x.test/a.m4a")]
    [InlineData("https://x.test/a.aac")]
    [InlineData("https://x.test/a.ogg")]
    [InlineData("https://x.test/a.opus")]
    [InlineData("https://x.test/A.MP3")]
    public void IsAudioUrl_accepts_known_extensions(string url)
        => RssParser.IsAudioUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("https://x.test/page.html")]
    [InlineData("https://x.test/video.mp4")]
    [InlineData("https://x.test/a.mp3?utm=1")]
    [InlineData("")]
    public void IsAudioUrl_rejects_everything_else(string url)
        => RssParser.IsAudioUrl(url).Should().BeFalse();

    // ── TryGetAudioUrl ──────────────────────────────────────────────────────

    [Fact]
    public void TryGetAudioUrl_reads_enclosure()
    {
        var item = FirstItem("""
            <title>ep</title>
            <enclosure url="https://cdn.test/ep1.mp3" type="audio/mpeg" length="123"/>
            """);

        RssParser.TryGetAudioUrl(item).Should().Be("https://cdn.test/ep1.mp3");
    }

    [Fact]
    public void TryGetAudioUrl_accepts_enclosure_without_type_when_url_looks_like_audio()
    {
        var item = FirstItem("""
            <title>ep</title>
            <enclosure url="https://cdn.test/ep1.m4a"/>
            """);

        RssParser.TryGetAudioUrl(item).Should().Be("https://cdn.test/ep1.m4a");
    }

    [Fact]
    public void TryGetAudioUrl_reads_media_content()
    {
        var item = FirstItem("""
            <title>ep</title>
            <media:content url="https://cdn.test/ep2.mp3" type="audio/mpeg"/>
            """);

        RssParser.TryGetAudioUrl(item).Should().Be("https://cdn.test/ep2.mp3");
    }

    [Fact]
    public void TryGetAudioUrl_returns_null_when_only_a_web_page_is_linked()
    {
        var item = FirstItem("""
            <title>ep</title>
            <link>https://example.test/episode-1</link>
            """);

        RssParser.TryGetAudioUrl(item).Should().BeNull();
    }

    // ── TryGetGuid ──────────────────────────────────────────────────────────

    [Fact]
    public void TryGetGuid_reads_guid_element()
    {
        var item = FirstItem("""
            <title>ep</title>
            <guid isPermaLink="false">tag:example.test,2026:ep-1</guid>
            """);

        RssParser.TryGetGuid(item).Should().Be("tag:example.test,2026:ep-1");
    }

    [Fact]
    public void TryGetGuid_ignores_an_empty_guid()
    {
        var item = FirstItem("""
            <title>ep</title>
            <guid>   </guid>
            """);

        RssParser.TryGetGuid(item).Should().BeNull();
    }

    [Fact]
    public void TryGetGuid_returns_null_when_absent()
        => RssParser.TryGetGuid(FirstItem("<title>ep</title>")).Should().BeNull();

    // ── TryGetChaptersUrl ───────────────────────────────────────────────────

    [Fact]
    public void TryGetChaptersUrl_reads_podcast_namespace_element()
    {
        var item = FirstItem("""
            <title>ep</title>
            <podcast:chapters url="https://cdn.test/ep1.chapters.json" type="application/json+chapters"/>
            """);

        RssParser.TryGetChaptersUrl(item).Should().Be("https://cdn.test/ep1.chapters.json");
    }

    [Fact]
    public void TryGetChaptersUrl_returns_null_when_absent()
        => RssParser.TryGetChaptersUrl(FirstItem("<title>ep</title>")).Should().BeNull();

    // ── TryGetDurationMs ────────────────────────────────────────────────────

    [Fact]
    public void TryGetDurationMs_reads_itunes_duration()
    {
        var item = FirstItem("""
            <title>ep</title>
            <itunes:duration>01:02:03</itunes:duration>
            """);

        RssParser.TryGetDurationMs(item).Should().Be(3_723_000L);
    }

    [Fact]
    public void TryGetDurationMs_reads_enclosure_duration_attribute()
    {
        var item = FirstItem("""
            <title>ep</title>
            <enclosure url="https://cdn.test/ep1.mp3" type="audio/mpeg" duration="600"/>
            """);

        RssParser.TryGetDurationMs(item).Should().Be(600_000L);
    }

    [Fact]
    public void TryGetDurationMs_returns_null_when_absent()
        => RssParser.TryGetDurationMs(FirstItem("<title>ep</title>")).Should().BeNull();

    [Fact]
    public void TryGetDurationMs_returns_null_on_an_unparsable_value()
    {
        var item = FirstItem("""
            <title>ep</title>
            <itunes:duration>whenever</itunes:duration>
            """);

        RssParser.TryGetDurationMs(item).Should().BeNull();
    }

    // ── ParseDate ───────────────────────────────────────────────────────────

    [Fact]
    public void ParseDate_reads_rfc822_pubdate()
    {
        var item = FirstItem("""
            <title>ep</title>
            <pubDate>Tue, 10 Feb 2026 08:30:00 +0000</pubDate>
            """);

        var d = RssParser.ParseDate(item);

        d.Should().NotBeNull();
        d!.Value.UtcDateTime.Should().Be(new DateTime(2026, 2, 10, 8, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseDate_returns_null_when_absent()
        => RssParser.ParseDate(FirstItem("<title>ep</title>")).Should().BeNull();
}
