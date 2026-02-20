using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

public sealed class GpodderSyncServiceTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "podliner-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    static (GpodderSyncService svc, FakeGpodderClient client, GpodderStore store,
            PlaybackCoordinator pc, AppData data, string dir) MakeSetup()
    {
        var dir    = TempDir();
        var data   = new AppData { NetworkOnline = true };
        var player = new FakeAudioPlayer();
        var pc     = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink());
        var store  = new GpodderStore(dir);
        store.Load();
        store.Current.ServerUrl = "https://gpodder.net";
        store.Current.Username  = "user";
        store.Current.Password  = "pass";
        var client = new FakeGpodderClient();
        var svc    = new GpodderSyncService(store, client, data, pc);
        return (svc, client, store, pc, data, dir);
    }

    // ── login flow ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_persists_credentials_on_success()
    {
        var (svc, client, store, _, _, dir) = MakeSetup();
        try
        {
            client.LoginResult = true;

            var (ok, _) = await svc.LoginAsync("https://newserver.com", "newuser", "newpass");

            ok.Should().BeTrue();
            store.Current.ServerUrl.Should().Be("https://newserver.com");
            store.Current.Username.Should().Be("newuser");
            store.Current.Password.Should().Be("newpass");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoginAsync_returns_false_when_offline()
    {
        var (svc, _, _, _, data, dir) = MakeSetup();
        try
        {
            data.NetworkOnline = false;

            var (ok, msg) = await svc.LoginAsync("https://gpodder.net", "user", "pass");

            ok.Should().BeFalse();
            msg.Should().Contain("offline");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoginAsync_returns_false_when_client_rejects()
    {
        var (svc, client, _, _, _, dir) = MakeSetup();
        try
        {
            client.LoginResult = false;

            var (ok, msg) = await svc.LoginAsync("https://gpodder.net", "user", "pass");

            ok.Should().BeFalse();
            msg.Should().Contain("failed");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    // ── subscription diff logic ───────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_puts_new_local_feed_in_add_array()
    {
        var (svc, client, store, _, data, dir) = MakeSetup();
        try
        {
            data.Feeds.Add(new Feed { Title = "New Feed", Url = "https://new.com/rss" });
            // LastKnownServerFeeds is empty → the URL is new
            store.Current.LastKnownServerFeeds = new();

            await svc.PushAsync();

            client.SubsPushes.Should().HaveCount(1);
            client.SubsPushes[0].add.Should().Contain("https://new.com/rss");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_puts_stale_server_feed_in_remove_array()
    {
        var (svc, client, store, _, data, dir) = MakeSetup();
        try
        {
            // Server knows about a URL that is not in local feeds
            store.Current.LastKnownServerFeeds = new() { "https://stale.com/rss" };
            // data.Feeds is empty

            await svc.PushAsync();

            client.SubsPushes.Should().HaveCount(1);
            client.SubsPushes[0].remove.Should().Contain("https://stale.com/rss");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_skips_subscription_call_when_sets_are_equal()
    {
        var (svc, client, store, _, data, dir) = MakeSetup();
        try
        {
            data.Feeds.Add(new Feed { Title = "Feed", Url = "https://feed.com/rss" });
            store.Current.LastKnownServerFeeds = new() { "https://feed.com/rss" };

            await svc.PushAsync();

            client.SubsPushes.Should().BeEmpty();
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_updates_LastKnownServerFeeds_after_push()
    {
        var (svc, client, store, _, data, dir) = MakeSetup();
        try
        {
            data.Feeds.Add(new Feed { Title = "Feed", Url = "https://feed.com/rss" });
            store.Current.LastKnownServerFeeds = new();

            await svc.PushAsync();

            store.Current.LastKnownServerFeeds.Should().Contain("https://feed.com/rss");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_updates_SubsTimestamp_from_push_response()
    {
        var (svc, client, store, _, data, dir) = MakeSetup();
        try
        {
            data.Feeds.Add(new Feed { Title = "Feed", Url = "https://feed.com/rss" });
            store.Current.LastKnownServerFeeds = new();
            client.NextTimestamp = 42;

            await svc.PushAsync();

            store.Current.SubsTimestamp.Should().Be(42);
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    // ── pending action queue ──────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_clears_pending_actions_on_success()
    {
        var (svc, client, store, _, _, dir) = MakeSetup();
        try
        {
            store.Current.PendingActions.AddRange([
                new PendingGpodderAction { PodcastUrl = "p1", EpisodeUrl = "e1", Timestamp = DateTimeOffset.UtcNow },
                new PendingGpodderAction { PodcastUrl = "p2", EpisodeUrl = "e2", Timestamp = DateTimeOffset.UtcNow },
            ]);

            await svc.PushAsync();

            store.Current.PendingActions.Should().BeEmpty();
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_preserves_pending_actions_on_failure()
    {
        var (svc, client, store, _, _, dir) = MakeSetup();
        try
        {
            // No subscription changes (empty on both sides) so only episode push is called
            store.Current.LastKnownServerFeeds = new();
            store.Current.PendingActions.AddRange([
                new PendingGpodderAction { PodcastUrl = "p1", EpisodeUrl = "e1", Timestamp = DateTimeOffset.UtcNow },
                new PendingGpodderAction { PodcastUrl = "p2", EpisodeUrl = "e2", Timestamp = DateTimeOffset.UtcNow },
            ]);
            client.ThrowOnPush = true;

            var (ok, _) = await svc.PushAsync();

            ok.Should().BeFalse();
            store.Current.PendingActions.Should().HaveCount(2);
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_returns_false_when_not_configured()
    {
        var (svc, _, store, _, _, dir) = MakeSetup();
        try
        {
            store.Current.ServerUrl = null;
            store.Current.Username  = null;
            store.Current.Password  = null;

            var (ok, msg) = await svc.PushAsync();

            ok.Should().BeFalse();
            msg.Should().Contain("not configured");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_returns_false_when_offline()
    {
        var (svc, _, _, _, data, dir) = MakeSetup();
        try
        {
            data.NetworkOnline = false;

            var (ok, msg) = await svc.PushAsync();

            ok.Should().BeFalse();
            msg.Should().Contain("offline");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    // ── delta pull ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PullAsync_adds_placeholder_feed_for_url_in_delta_add()
    {
        var (svc, client, _, _, data, dir) = MakeSetup();
        try
        {
            client.NextDelta = new SubscriptionDelta(["https://new.com/feed"], [], 77);

            await svc.PullAsync();

            data.Feeds.Should().HaveCount(1);
            data.Feeds[0].Url.Should().Be("https://new.com/feed");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PullAsync_skips_feed_already_present_locally()
    {
        var (svc, client, _, _, data, dir) = MakeSetup();
        try
        {
            data.Feeds.Add(new Feed { Title = "Existing", Url = "https://existing.com/feed" });
            client.NextDelta = new SubscriptionDelta(["https://existing.com/feed"], [], 77);

            await svc.PullAsync();

            data.Feeds.Should().HaveCount(1);
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PullAsync_updates_SubsTimestamp_to_delta_timestamp()
    {
        var (svc, client, store, _, _, dir) = MakeSetup();
        try
        {
            client.NextDelta = new SubscriptionDelta([], [], 77);

            await svc.PullAsync();

            store.Current.SubsTimestamp.Should().Be(77);
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    // ── episode action tracking ───────────────────────────────────────────────

    [Fact]
    public void QueuePlayAction_enqueued_when_session_changes()
    {
        var (svc, _, store, pc, data, dir) = MakeSetup();
        try
        {
            var feedId = Guid.NewGuid();
            data.Feeds.Add(new Feed { Id = feedId, Title = "Feed", Url = "https://feed.com/rss" });

            var ep1 = new Episode { FeedId = feedId, Title = "Ep1", AudioUrl = "https://ep1.com/audio.mp3" };
            var ep2 = new Episode { FeedId = feedId, Title = "Ep2", AudioUrl = "https://ep2.com/audio.mp3" };
            data.Episodes.AddRange([ep1, ep2]);

            // Play ep1: snapshot fires → _lastSessionId set to 1, _lastEpisodeId = ep1.Id
            pc.Play(ep1);
            // Play ep2: snapshot fires → session changed → QueuePlayAction(ep1.Id)
            pc.Play(ep2);

            store.Current.PendingActions.Should().HaveCount(1);
            store.Current.PendingActions[0].EpisodeUrl.Should().Be(ep1.AudioUrl);
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void QueuePlayAction_enqueued_when_episode_ends()
    {
        var (svc, _, store, pc, data, dir) = MakeSetup();
        try
        {
            var feedId = Guid.NewGuid();
            data.Feeds.Add(new Feed { Id = feedId, Title = "Feed", Url = "https://feed.com/rss" });

            var ep = new Episode
            {
                FeedId    = feedId,
                Title     = "Ep",
                AudioUrl  = "https://ep.com/audio.mp3",
                DurationMs = 60_000,
            };
            data.Episodes.Add(ep);

            // Play: snapshot fires → _lastSessionId set, _lastEpisodeId = ep.Id
            pc.Play(ep);

            // Tick at near-end: IsPlaying=false, remain=1s ≤ 2000ms → IsEndReached=true
            pc.PersistProgressTick(
                new PlayerState
                {
                    IsPlaying = false,
                    Position  = TimeSpan.FromSeconds(59),
                    Length    = TimeSpan.FromSeconds(60),
                    Speed     = 1.0,
                },
                _ => { },
                data.Episodes);

            store.Current.PendingActions.Should().HaveCount(1);
            store.Current.PendingActions[0].EpisodeUrl.Should().Be(ep.AudioUrl);
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }
}
