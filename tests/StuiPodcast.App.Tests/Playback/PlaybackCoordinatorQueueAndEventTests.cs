using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Playback;

public sealed class PlaybackCoordinatorQueueAndEventTests
{
    static (AppData data, PlaybackCoordinator pc, Guid feedId, FakeEpisodeStore episodes, FakeQueueService queue) MakeSetup()
    {
        var data     = new AppData();
        var player   = new FakeAudioPlayer();
        var episodes = new FakeEpisodeStore();
        var queue    = new FakeQueueService();
        var feeds    = new FakeFeedStore();
        var pc       = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink(), episodes, queue);
        var feedId   = Guid.NewGuid();
        feeds.Seed(new Feed { Id = feedId, Title = "Feed" });
        return (data, pc, feedId, episodes, queue);
    }

    static Episode MakeEpisode(Guid feedId, int daysAgo = 1) => new()
    {
        FeedId   = feedId,
        Title    = $"Ep-{daysAgo}",
        AudioUrl = $"https://example.com/{daysAgo}.mp3",
        PubDate  = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        DurationMs = 120_000,
    };

    // ── ConsumeQueueUpToInclusive via Play() ──────────────────────────────────

    [Fact]
    public void Play_removes_all_queue_entries_up_to_and_including_played_episode()
    {
        var (_, pc, feedId, episodes, queue) = MakeSetup();
        var epA = MakeEpisode(feedId, 1);
        var epB = MakeEpisode(feedId, 2); // the one we will play
        var epC = MakeEpisode(feedId, 3);
        episodes.Seed(epA, epB, epC);
        queue.Seed(epA.Id, epB.Id, epC.Id);

        pc.Play(epB);

        // epA and epB consumed; epC remains
        queue.Snapshot().Should().Equal([epC.Id]);
    }

    [Fact]
    public void Play_leaves_queue_unchanged_when_episode_not_in_queue()
    {
        var (_, pc, feedId, episodes, queue) = MakeSetup();
        var epA = MakeEpisode(feedId, 1);
        var epB = MakeEpisode(feedId, 2);
        var epC = MakeEpisode(feedId, 3); // played, but NOT in queue
        episodes.Seed(epA, epB, epC);
        queue.Seed(epA.Id, epB.Id);

        pc.Play(epC);

        queue.Snapshot().Should().Equal([epA.Id, epB.Id]);
    }

    [Fact]
    public void Play_empties_queue_when_last_entry_is_played()
    {
        var (_, pc, feedId, episodes, queue) = MakeSetup();
        var epA = MakeEpisode(feedId, 1);
        var epB = MakeEpisode(feedId, 2);
        episodes.Seed(epA, epB);
        queue.Seed(epA.Id, epB.Id);

        pc.Play(epB); // epB is last → entire queue consumed

        queue.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Play_fires_QueueChanged_when_entries_are_consumed()
    {
        var (_, pc, feedId, episodes, queue) = MakeSetup();
        var epA = MakeEpisode(feedId, 1);
        var epB = MakeEpisode(feedId, 2);
        episodes.Seed(epA, epB);
        queue.Seed(epA.Id, epB.Id);

        int eventCount = 0;
        pc.QueueChanged += () => eventCount++;

        pc.Play(epB); // consumes both → one QueueChanged fired

        eventCount.Should().Be(1);
    }

    [Fact]
    public void Play_does_not_fire_QueueChanged_when_episode_not_in_queue()
    {
        var (_, pc, feedId, episodes, queue) = MakeSetup();
        var epA = MakeEpisode(feedId, 1);
        var epB = MakeEpisode(feedId, 2);
        episodes.Seed(epA, epB);
        queue.Seed(epA.Id);

        int eventCount = 0;
        pc.QueueChanged += () => eventCount++;

        pc.Play(epB); // epB not in queue → no QueueChanged

        eventCount.Should().Be(0);
    }

    // ── SnapshotAvailable event ────────────────────────────────────────────────

    [Fact]
    public void Play_fires_SnapshotAvailable_with_episode_id_and_zero_position()
    {
        var (_, pc, feedId, episodes, _) = MakeSetup();
        var ep = MakeEpisode(feedId);
        episodes.Seed(ep);

        PlaybackSnapshot? received = null;
        pc.SnapshotAvailable += s => received = s;

        pc.Play(ep);

        received.Should().NotBeNull();
        received!.Value.EpisodeId.Should().Be(ep.Id);
        received.Value.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void PersistProgressTick_fires_SnapshotAvailable_with_current_position()
    {
        var (_, pc, feedId, episodes, _) = MakeSetup();
        var ep = MakeEpisode(feedId);
        episodes.Seed(ep);
        pc.Play(ep);

        // Capture only the tick-fired snapshot (not the initial one from Play)
        PlaybackSnapshot? tickSnapshot = null;
        pc.SnapshotAvailable += s => tickSnapshot = s;

        pc.PersistProgressTick(
            new PlayerState { IsPlaying = true, Position = TimeSpan.FromSeconds(30), Length = TimeSpan.FromSeconds(120), Speed = 1.0 },
            _ => { });

        tickSnapshot.Should().NotBeNull();
        tickSnapshot!.Value.EpisodeId.Should().Be(ep.Id);
        tickSnapshot.Value.IsPlaying.Should().BeTrue();
        tickSnapshot.Value.Position.Should().Be(TimeSpan.FromSeconds(30));
    }
}
