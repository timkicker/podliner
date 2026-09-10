using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Playback;

// StatusChanged drives the "loading…" overlay in the player panel. It had no
// coverage at all, which is how the two-stage stall watch ended up firing
// SlowNetwork twice instead of escalating to VerySlowNetwork.
public sealed class PlaybackCoordinatorStatusTests
{
    private static (PlaybackCoordinator pc, FakeAudioPlayer player, List<PlaybackStatus> seen, Guid feedId) MakeSetup()
    {
        var data = new AppData();
        var player = new FakeAudioPlayer();
        var episodes = new FakeEpisodeStore();
        var queue = new FakeQueueService();
        var pc = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink(), episodes, queue);

        var seen = new List<PlaybackStatus>();
        pc.StatusChanged += s => { lock (seen) seen.Add(s); };

        return (pc, player, seen, Guid.NewGuid());
    }

    private static Episode MakeEpisode(Guid feedId, long durationMs = 600_000) => new()
    {
        FeedId = feedId,
        Title = "Episode",
        AudioUrl = "https://example.com/ep.mp3",
        DurationMs = durationMs,
        PubDate = DateTimeOffset.UtcNow.AddDays(-1),
    };

    private static void Tick(PlaybackCoordinator pc, long posMs, long lenMs, bool playing = true)
        => pc.PersistProgressTick(
            new PlayerState
            {
                IsPlaying = playing,
                Position = TimeSpan.FromMilliseconds(posMs),
                Length = TimeSpan.FromMilliseconds(lenMs),
                Speed = 1.0,
            },
            _ => { });

    [Fact]
    public void Play_reports_loading_first()
    {
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;

        pc.Play(MakeEpisode(feedId));

        lock (seen) seen.Should().StartWith(new[] { PlaybackStatus.Loading });
    }

    [Fact]
    public void The_first_advancing_tick_reports_playing()
    {
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;
        pc.Play(MakeEpisode(feedId));

        Tick(pc, posMs: 1_000, lenMs: 600_000);

        lock (seen) seen.Should().Contain(PlaybackStatus.Playing);
    }

    [Fact]
    public void A_tick_at_position_zero_does_not_clear_loading()
    {
        // LibVLC reports IsPlaying=true while still filling its buffer, so
        // only a position that actually moved counts as playing.
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;
        pc.Play(MakeEpisode(feedId));

        Tick(pc, posMs: 0, lenMs: 600_000);

        lock (seen) seen.Should().NotContain(PlaybackStatus.Playing);
    }

    [Fact]
    public void Playing_is_reported_once_per_session()
    {
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;
        pc.Play(MakeEpisode(feedId));

        Tick(pc, 1_000, 600_000);
        Tick(pc, 2_000, 600_000);
        Tick(pc, 3_000, 600_000);

        lock (seen) seen.Count(s => s == PlaybackStatus.Playing).Should().Be(1);
    }

    [Fact]
    public void Reaching_the_end_reports_ended()
    {
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;
        pc.Play(MakeEpisode(feedId, durationMs: 10_000));

        Tick(pc, 1_000, 10_000);
        Tick(pc, 10_000, 10_000, playing: false);

        lock (seen) seen.Should().Contain(PlaybackStatus.Ended);
    }

    [Fact]
    public void Ended_is_reported_once_per_session()
    {
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;
        pc.Play(MakeEpisode(feedId, durationMs: 10_000));

        Tick(pc, 1_000, 10_000);
        Tick(pc, 10_000, 10_000, playing: false);
        Tick(pc, 10_000, 10_000, playing: false);

        lock (seen) seen.Count(s => s == PlaybackStatus.Ended).Should().Be(1);
    }

    [Fact]
    public void A_new_play_starts_a_fresh_status_sequence()
    {
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;

        pc.Play(MakeEpisode(feedId));
        Tick(pc, 1_000, 600_000);
        lock (seen) seen.Clear();

        pc.Play(MakeEpisode(feedId));

        lock (seen) seen.Should().StartWith(new[] { PlaybackStatus.Loading });
    }

    [Fact]
    public void Playing_is_reported_again_after_a_restart()
    {
        var (pc, _, seen, feedId) = MakeSetup();
        using var _pc = pc;

        pc.Play(MakeEpisode(feedId));
        Tick(pc, 1_000, 600_000);
        pc.Play(MakeEpisode(feedId));
        Tick(pc, 1_000, 600_000);

        lock (seen) seen.Count(s => s == PlaybackStatus.Playing).Should().Be(2);
    }

    [Fact]
    public void The_two_stall_stages_are_distinct_states()
    {
        // SlowNetwork and VerySlowNetwork must not be the same value, or the
        // second stage of the stall watch is indistinguishable from the first
        // and the UI can never escalate its message.
        PlaybackStatus.VerySlowNetwork.Should().NotBe(PlaybackStatus.SlowNetwork);
    }

    [Fact]
    public void Dispose_stops_the_stall_watch_from_firing()
    {
        var (pc, _, seen, feedId) = MakeSetup();

        pc.Play(MakeEpisode(feedId));
        pc.Dispose();

        lock (seen) seen.Should().NotContain(PlaybackStatus.SlowNetwork);
        lock (seen) seen.Should().NotContain(PlaybackStatus.VerySlowNetwork);
    }
}
