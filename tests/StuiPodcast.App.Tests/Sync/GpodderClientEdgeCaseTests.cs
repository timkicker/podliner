using FluentAssertions;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Infra.Sync;
using System.Net;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

public sealed class GpodderClientEdgeCaseTests
{
    static (FakeHttpHandler handler, GpodderClient client) MakeClient()
    {
        var handler = new FakeHttpHandler();
        var client = new GpodderClient(handler);
        client.Configure("https://gpodder.net", "user", "pass");
        return (handler, client);
    }

    [Fact]
    public async Task RegisterDeviceAsync_throws_on_server_error()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => client.RegisterDeviceAsync("user", "device");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RegisterDeviceAsync_sends_PUT_with_device_json()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        await client.RegisterDeviceAsync("user", "my-device");

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Method.Should().Be(HttpMethod.Put);
        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Podliner");
        body.Should().Contain("desktop");
    }

    [Fact]
    public async Task GetSubscriptionDeltaAsync_throws_on_404()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => client.GetSubscriptionDeltaAsync("user", "device", 0);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task PushSubscriptionChangesAsync_throws_on_server_error()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => client.PushSubscriptionChangesAsync("user", "device", ["feed1"], []);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetEpisodeActionsAsync_returns_empty_on_no_actions()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { actions = Array.Empty<object>(), timestamp = 5L }));

        var result = await client.GetEpisodeActionsAsync("user", 0);

        result.Actions.Should().BeEmpty();
        result.Timestamp.Should().Be(5L);
    }

    [Fact]
    public async Task GetEpisodeActionsAsync_parses_episode_action_fields()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new
        {
            actions = new[]
            {
                new
                {
                    podcast = "https://feed.com/rss",
                    episode = "https://feed.com/ep.mp3",
                    action = "play",
                    timestamp = "2024-06-01T12:00:00",
                    position = 120,
                    total = 600
                }
            },
            timestamp = 10L
        }));

        var result = await client.GetEpisodeActionsAsync("user", 0);

        result.Actions.Should().HaveCount(1);
        var a = result.Actions[0];
        a.PodcastUrl.Should().Be("https://feed.com/rss");
        a.EpisodeUrl.Should().Be("https://feed.com/ep.mp3");
        a.Action.Should().Be("play");
        a.Position.Should().Be(120);
        a.Total.Should().Be(600);
    }

    [Fact]
    public async Task LoginAsync_encodes_username_in_url()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { }));

        await client.LoginAsync("https://gpodder.net", "user with spaces", "pass");

        handler.Requests[0].RequestUri!.AbsoluteUri
            .Should().Contain("user%20with%20spaces");
    }

    [Fact]
    public async Task PushEpisodeActionsAsync_throws_on_server_error()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => client.PushEpisodeActionsAsync("user", []);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task LoginAsync_records_status_on_404_for_diagnostics()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });

        var ok = await client.LoginAsync("https://gpodder.net", "user", "pass");

        ok.Should().BeFalse();
        client.LastLoginStatus.Should().Be(404);
        client.LastLoginReason.Should().Be("Not Found");
    }

    [Fact]
    public async Task LoginAsync_records_status_on_success()
    {
        var (handler, client) = MakeClient();
        handler.Enqueue(FakeHttpHandler.Json(new { }));

        var ok = await client.LoginAsync("https://gpodder.net", "user", "pass");

        ok.Should().BeTrue();
        client.LastLoginStatus.Should().Be(200);
    }

    [Fact]
    public async Task Configure_trims_trailing_slash()
    {
        var handler = new FakeHttpHandler();
        var client = new GpodderClient(handler);
        client.Configure("https://gpodder.net///", "user", "pass");
        handler.Enqueue(FakeHttpHandler.Json(new { add = Array.Empty<string>(), remove = Array.Empty<string>(), timestamp = 1L }));

        await client.GetSubscriptionDeltaAsync("user", "device", 0);

        handler.Requests[0].RequestUri!.AbsoluteUri
            .Should().StartWith("https://gpodder.net/api/");
    }
}
