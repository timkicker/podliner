using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using StuiPodcast.Infra.Feeds;
using StuiPodcast.Infra.Tests.Fakes;
using Xunit;

namespace StuiPodcast.Infra.Tests.Feeds;

public sealed class FeedHttpFetcherTests
{
    private const string Url = "https://example.test/feed.xml";
    private const string Xml = "<rss version=\"2.0\"><channel><title>T</title></channel></rss>";

    private static (FeedHttpFetcher fetcher, FakeHttpHandler handler) Make()
    {
        var handler = new FakeHttpHandler();
        return (new FeedHttpFetcher(handler), handler);
    }

    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FetchXmlAsync_returns_the_body()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueXml(Xml);

        (await fetcher.FetchXmlAsync(Url)).Should().Be(Xml);
    }

    [Fact]
    public async Task FetchAsync_reports_success()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueXml(Xml);

        var r = await fetcher.FetchAsync(Url, null, null);

        r.IsOk.Should().BeTrue();
        r.IsFailure.Should().BeFalse();
        r.IsNotModified.Should().BeFalse();
        r.Xml.Should().Be(Xml);
    }

    [Fact]
    public async Task FetchAsync_captures_the_etag()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Xml, Encoding.UTF8, "application/rss+xml")
        };
        resp.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
        handler.Enqueue(resp);

        var r = await fetcher.FetchAsync(Url, null, null);

        r.Etag.Should().Be("\"abc123\"");
    }

    [Fact]
    public async Task FetchAsync_captures_last_modified_as_rfc1123()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Xml, Encoding.UTF8, "application/rss+xml")
        };
        resp.Content.Headers.LastModified = new DateTimeOffset(2026, 2, 10, 8, 30, 0, TimeSpan.Zero);
        handler.Enqueue(resp);

        var r = await fetcher.FetchAsync(Url, null, null);

        r.LastModified.Should().Be("Tue, 10 Feb 2026 08:30:00 GMT");
    }

    // ── conditional GET ─────────────────────────────────────────────────────

    [Fact]
    public async Task Conditional_headers_are_sent_when_supplied()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueStatus(HttpStatusCode.NotModified);

        await fetcher.FetchAsync(Url, "\"etag-1\"", "Tue, 10 Feb 2026 08:30:00 GMT");

        var req = handler.Requests.Single();
        req.Headers.GetValues("If-None-Match").Should().ContainSingle().Which.Should().Be("\"etag-1\"");
        req.Headers.GetValues("If-Modified-Since").Should().ContainSingle();
    }

    [Fact]
    public async Task No_conditional_headers_are_sent_on_a_first_fetch()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueXml(Xml);

        await fetcher.FetchAsync(Url, null, null);

        var req = handler.Requests.Single();
        req.Headers.Contains("If-None-Match").Should().BeFalse();
        req.Headers.Contains("If-Modified-Since").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_etag_is_not_sent(string? etag)
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueXml(Xml);

        await fetcher.FetchAsync(Url, etag, null);

        handler.Requests.Single().Headers.Contains("If-None-Match").Should().BeFalse();
    }

    [Fact]
    public async Task A_304_is_reported_as_not_modified_with_no_body()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueStatus(HttpStatusCode.NotModified);

        var r = await fetcher.FetchAsync(Url, "\"etag-1\"", null);

        r.IsNotModified.Should().BeTrue();
        r.IsOk.Should().BeFalse();
        r.IsFailure.Should().BeFalse();
        r.Xml.Should().BeNull();
        r.Failure.Should().Be(FetchFailure.None);
    }

    [Fact]
    public async Task A_304_does_not_clobber_the_cached_freshness_hints()
    {
        // The caller keeps its stored ETag; a 304 must not hand back nulls
        // that would overwrite it.
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueStatus(HttpStatusCode.NotModified);

        var r = await fetcher.FetchAsync(Url, "\"etag-1\"", null);

        r.IsNotModified.Should().BeTrue();
        r.Etag.Should().BeNull();
        r.LastModified.Should().BeNull();
    }

    // ── failures ────────────────────────────────────────────────────────────

    // FetchFailure is internal, so it cannot appear in a public test
    // signature; the expected value travels as its underlying int.
    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)FetchFailure.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, (int)FetchFailure.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, (int)FetchFailure.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, (int)FetchFailure.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, (int)FetchFailure.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, (int)FetchFailure.ClientError)]
    [InlineData(HttpStatusCode.TooManyRequests, (int)FetchFailure.ClientError)]
    public async Task Http_errors_map_to_a_failure_category(HttpStatusCode status, int expectedFailure)
    {
        var expected = (FetchFailure)expectedFailure;
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueStatus(status);

        var r = await fetcher.FetchAsync(Url, null, null);

        r.IsFailure.Should().BeTrue();
        r.Failure.Should().Be(expected);
        r.HttpStatus.Should().Be((int)status);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_such()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueThrowing(new TaskCanceledException("timed out"));

        var r = await fetcher.FetchAsync(Url, null, null);

        r.Failure.Should().Be(FetchFailure.Timeout);
        r.FailureDetail.Should().Be("timed out");
    }

    [Fact]
    public async Task An_unreachable_host_is_reported_as_such()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueThrowing(new HttpRequestException("no such host"));

        var r = await fetcher.FetchAsync(Url, null, null);

        r.Failure.Should().Be(FetchFailure.Unreachable);
        r.FailureDetail.Should().Contain("no such host");
    }

    [Fact]
    public async Task Any_other_exception_is_reported_as_other()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueThrowing(new InvalidOperationException("weird"));

        var r = await fetcher.FetchAsync(Url, null, null);

        r.Failure.Should().Be(FetchFailure.Other);
    }

    [Fact]
    public async Task FetchXmlAsync_returns_null_on_failure()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueStatus(HttpStatusCode.NotFound);

        (await fetcher.FetchXmlAsync(Url)).Should().BeNull();
    }

    [Fact]
    public async Task A_failure_never_throws_out_of_the_fetcher()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueThrowing(new HttpRequestException("boom"));

        var act = async () => await fetcher.FetchAsync(Url, null, null);

        await act.Should().NotThrowAsync();
    }

    // ── request shape ───────────────────────────────────────────────────────

    [Fact]
    public async Task Requests_identify_podliner_in_the_user_agent()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueXml(Xml);

        await fetcher.FetchAsync(Url, null, null);

        handler.Requests.Single().Headers.UserAgent.ToString()
            .Should().Contain("podliner");
    }

    [Fact]
    public async Task Requests_accept_rss_and_xml()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueXml(Xml);

        await fetcher.FetchAsync(Url, null, null);

        var accept = handler.Requests.Single().Headers.Accept.Select(a => a.MediaType).ToArray();
        accept.Should().Contain("application/rss+xml");
        accept.Should().Contain("application/xml");
    }

    [Fact]
    public async Task Fetching_uses_GET()
    {
        var (fetcher, handler) = Make();
        using var _f = fetcher;
        handler.EnqueueXml(Xml);

        await fetcher.FetchAsync(Url, null, null);

        handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
    }

    // ── lifecycle ───────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_twice_is_safe()
    {
        var (fetcher, _) = Make();
        fetcher.Dispose();

        var act = () => fetcher.Dispose();

        act.Should().NotThrow();
    }
}
