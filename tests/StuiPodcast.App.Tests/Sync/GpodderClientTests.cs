using FluentAssertions;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;
using System.Net;
using System.Text.Json;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

public sealed class GpodderClientTests
{
    static (FakeHttpHandler handler, GpodderClient client) MakeClient()
    {
        var handler = new FakeHttpHandler();
        var client  = new GpodderClient(handler);
        client.Configure("https://gpodder.net", "user", "pass");
        return (handler, client);
    }

    [Fact]
    public async Task LoginAsync_returns_true_on_200_OK()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { }));

        var result = await client.LoginAsync("https://gpodder.net", "user", "pass");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task LoginAsync_returns_false_on_401()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.LoginAsync("https://gpodder.net", "user", "pass");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task LoginAsync_sends_Basic_Auth_header()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { }));

        await client.LoginAsync("https://gpodder.net", "user", "pass");

        var auth = handler.Requests[0].Headers.Authorization;
        auth.Should().NotBeNull();
        auth!.Scheme.Should().Be("Basic");
    }

    [Fact]
    public async Task GetSubscriptionDeltaAsync_parses_add_remove_and_timestamp()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new
        {
            add       = new[] { "url1" },
            remove    = new[] { "url2" },
            timestamp = 9999L
        }));

        var delta = await client.GetSubscriptionDeltaAsync("user", "podliner-device", 0);

        delta.Add.Should().Equal("url1");
        delta.Remove.Should().Equal("url2");
        delta.Timestamp.Should().Be(9999L);
    }

    [Fact]
    public async Task GetSubscriptionDeltaAsync_handles_empty_arrays()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new
        {
            add       = Array.Empty<string>(),
            remove    = Array.Empty<string>(),
            timestamp = 1L
        }));

        var delta = await client.GetSubscriptionDeltaAsync("user", "podliner-device", 0);

        delta.Add.Should().BeEmpty();
        delta.Remove.Should().BeEmpty();
        delta.Timestamp.Should().Be(1L);
    }

    [Fact]
    public async Task PushSubscriptionChangesAsync_sends_correct_body_and_returns_timestamp()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { timestamp = 42L }));

        var result = await client.PushSubscriptionChangesAsync(
            "user", "podliner-device", ["https://feed1.com/rss"], ["https://old.com/rss"]);

        result.Should().Be(42L);

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("add").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("remove").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task PushEpisodeActionsAsync_serializes_required_fields_per_api_spec()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { timestamp = 1L }));

        var action = new PendingGpodderAction
        {
            PodcastUrl = "https://podcast.com/feed",
            EpisodeUrl = "https://podcast.com/ep.mp3",
            Action     = "play",
            Timestamp  = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Position   = 120,
            Total      = 600,
        };

        await client.PushEpisodeActionsAsync("user", [action]);

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var elem = doc.RootElement[0];

        elem.GetProperty("podcast").GetString().Should().Be("https://podcast.com/feed");
        elem.GetProperty("episode").GetString().Should().Be("https://podcast.com/ep.mp3");
        elem.GetProperty("action").GetString().Should().Be("play");
        elem.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PushEpisodeActionsAsync_omits_null_optional_fields()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { timestamp = 1L }));

        var action = new PendingGpodderAction
        {
            PodcastUrl = "https://podcast.com/feed",
            EpisodeUrl = "https://podcast.com/ep.mp3",
            Action     = "play",
            Timestamp  = DateTimeOffset.UtcNow,
            Started    = null,
            Position   = null,
            Total      = null,
        };

        await client.PushEpisodeActionsAsync("user", [action]);

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().NotContain("\"started\"");
        body.Should().NotContain("\"total\"");
        body.Should().NotContain("\"position\"");
    }
}
