using FluentAssertions;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;
using System.Net;
using System.Text.Json;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

public sealed class NextcloudGpodderClientTests
{
    const string Server = "https://cloud.example.com";

    static (FakeHttpHandler handler, NextcloudGpodderClient client) MakeClient()
    {
        var handler = new FakeHttpHandler();
        var client  = new NextcloudGpodderClient(handler);
        client.Configure(Server, "alice", "secret");
        return (handler, client);
    }

    // ── login probe ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_probes_subscriptions_endpoint()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { add = Array.Empty<string>(), remove = Array.Empty<string>(), timestamp = 0 }));

        var ok = await client.LoginAsync(Server, "alice", "secret");

        ok.Should().BeTrue();
        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsolutePath
            .Should().Be("/index.php/apps/gpoddersync/subscriptions");
    }

    [Fact]
    public async Task LoginAsync_returns_false_on_401()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ok = await client.LoginAsync(Server, "alice", "wrong");

        ok.Should().BeFalse();
        client.LastLoginStatus.Should().Be(401);
    }

    [Fact]
    public async Task LoginAsync_returns_false_on_404()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var ok = await client.LoginAsync(Server, "alice", "secret");

        ok.Should().BeFalse();
        client.LastLoginStatus.Should().Be(404);
    }

    [Fact]
    public async Task LoginAsync_sends_Basic_Auth()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { add = Array.Empty<string>(), remove = Array.Empty<string>(), timestamp = 0 }));

        await client.LoginAsync(Server, "alice", "secret");

        var auth = handler.Requests[0].Headers.Authorization;
        auth.Should().NotBeNull();
        auth!.Scheme.Should().Be("Basic");
    }

    // ── url normalization ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://cloud.example.com")]
    [InlineData("https://cloud.example.com/")]
    [InlineData("https://cloud.example.com/index.php")]
    [InlineData("https://cloud.example.com/index.php/")]
    public async Task Configure_normalizes_trailing_index_php(string input)
    {
        var handler = new FakeHttpHandler();
        var client  = new NextcloudGpodderClient(handler);
        client.Configure(input, "alice", "secret");
        handler.Enqueue(FakeHttpHandler.Json(new { add = Array.Empty<string>(), remove = Array.Empty<string>(), timestamp = 0 }));

        await client.GetSubscriptionDeltaAsync("alice", "desktop", 0);

        handler.Requests[0].RequestUri!.ToString()
            .Should().StartWith("https://cloud.example.com/index.php/apps/gpoddersync/");
        handler.Requests[0].RequestUri!.ToString()
            .Should().NotContain("index.php/index.php");
    }

    // ── device registration is a no-op ─────────────────────────────────────

    [Fact]
    public async Task RegisterDeviceAsync_makes_no_http_calls()
    {
        var (handler, client) = MakeClient();
        await client.RegisterDeviceAsync("alice", "desktop");
        handler.Requests.Should().BeEmpty();
    }

    // ── subscriptions ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSubscriptionDeltaAsync_parses_response()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new
        {
            add       = new[] { "https://a.example/feed", "https://b.example/feed" },
            remove    = new[] { "https://c.example/feed" },
            timestamp = 1234567L
        }));

        var delta = await client.GetSubscriptionDeltaAsync("alice", "desktop", 1000);

        delta.Add.Should().HaveCount(2);
        delta.Remove.Should().ContainSingle();
        delta.Timestamp.Should().Be(1234567);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/index.php/apps/gpoddersync/subscriptions");
        handler.Requests[0].RequestUri!.Query.Should().Contain("since=1000");
    }

    [Fact]
    public async Task PushSubscriptionChangesAsync_posts_to_subscription_change_create()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { timestamp = 99L }));

        var ts = await client.PushSubscriptionChangesAsync("alice", "desktop",
            new[] { "https://a.ex/feed" }, new[] { "https://b.ex/feed" });

        ts.Should().Be(99);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/index.php/apps/gpoddersync/subscription_change/create");

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"add\"");
        body.Should().Contain("\"remove\"");
        body.Should().Contain("https://a.ex/feed");
    }

    // ── episode actions ────────────────────────────────────────────────────

    [Fact]
    public async Task GetEpisodeActionsAsync_parses_actions()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new
        {
            actions = new object[]
            {
                new { podcast = "https://feed", episode = "https://ep1.mp3",
                      action = "play", timestamp = "2024-01-01T12:00:00",
                      position = 120, total = 600, started = 0 }
            },
            timestamp = 999L
        }));

        var r = await client.GetEpisodeActionsAsync("alice", 0);

        r.Actions.Should().ContainSingle();
        r.Actions[0].EpisodeUrl.Should().Be("https://ep1.mp3");
        r.Actions[0].Position.Should().Be(120);
        r.Timestamp.Should().Be(999);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/index.php/apps/gpoddersync/episode_action");
    }

    [Fact]
    public async Task PushEpisodeActionsAsync_posts_bare_array()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { timestamp = 42L }));

        var actions = new[]
        {
            new PendingGpodderAction
            {
                PodcastUrl = "https://feed",
                EpisodeUrl = "https://ep1.mp3",
                Action     = "play",
                Timestamp  = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero),
                Position   = 120, Total = 600
            }
        };

        var ts = await client.PushEpisodeActionsAsync("alice", actions);

        ts.Should().Be(42);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/index.php/apps/gpoddersync/episode_action/create");

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.TrimStart().Should().StartWith("[", "Nextcloud expects a bare JSON array, not an object wrapper");
        body.Should().Contain("\"action\":\"play\"");
        body.Should().Contain("\"position\":120");
    }
}
