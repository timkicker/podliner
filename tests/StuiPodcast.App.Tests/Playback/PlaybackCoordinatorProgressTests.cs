using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Playback;

public sealed class PlaybackCoordinatorProgressTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    static (AppData data, PlaybackCoordinator pc, Guid feedId) MakeSetup()
    {
        var data   = new AppData();
        var player = new FakeAudioPlayer();
        var pc     = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink());
        var feedId = Guid.NewGuid();
        data.Feeds.Add(new Feed { Id = feedId, Title = "Feed" });
        return (data, pc, feedId);
    }

    static Episode MakeEpisode(Guid feedId, long durationMs, int daysAgo = 1) => new()
    {
        FeedId   = feedId,
        Title    = $"Episode",
        AudioUrl = $"https://example.com/{daysAgo}.mp3",
        DurationMs = durationMs,
        PubDate  = DateTimeOffset.UtcNow.AddDays(-daysAgo),
    };

    // Simulate one progress tick for the given position/length (milliseconds).
    static void Tick(PlaybackCoordinator pc, AppData data, long posMs, long lenMs, bool playing = true)
        => pc.PersistProgressTick(
            new PlayerState
            {
                IsPlaying = playing,
                Position  = TimeSpan.FromMilliseconds(posMs),
                Length    = TimeSpan.FromMilliseconds(lenMs),
                Speed     = 1.0,
            },
            _ => { },
            data.Episodes);

    // ── position and duration persistence ────────────────────────────────────

    [Fact]
    public void PersistProgressTick_updates_episode_position_and_duration()
    {
        var (data, pc, feedId) = MakeSetup();
        var ep = MakeEpisode(feedId, durationMs: 0); // unknown duration initially
        data.Episodes.Add(ep);
        pc.Play(ep);

        Tick(pc, data, posMs: 45_000, lenMs: 120_000);

        ep.Progress.LastPosMs.Should().Be(45_000);
        ep.DurationMs.Should().Be(120_000);
    }

    // ── auto-mark-played: long episode (> 60 s) ──────────────────────────────

    [Fact]
    public void AutoMark_long_episode_at_90_percent_ratio()
    {
        var (data, pc, feedId) = MakeSetup();
        // 10-minute episode; 90% = 540 000 ms; remain = 60 s > 30 s → ratio triggers
        var ep = MakeEpisode(feedId, durationMs: 600_000);
        data.Episodes.Add(ep);
        pc.Play(ep);

        Tick(pc, data, posMs: 540_001, lenMs: 600_000);

        ep.ManuallyMarkedPlayed.Should().BeTrue();
    }

    [Fact]
    public void AutoMark_long_episode_not_marked_below_90_percent()
    {
        var (data, pc, feedId) = MakeSetup();
        var ep = MakeEpisode(feedId, durationMs: 600_000);
        data.Episodes.Add(ep);
        pc.Play(ep);

        // 89.9 %, remain = 60 001 ms > 30 s → neither threshold reached
        Tick(pc, data, posMs: 539_999, lenMs: 600_000);

        ep.ManuallyMarkedPlayed.Should().BeFalse();
    }

    [Fact]
    public void AutoMark_long_episode_at_30s_remaining_threshold()
    {
        var (data, pc, feedId) = MakeSetup();
        // 5-minute episode; posMs such that remain = 29 999 ms < 30 s
        var ep = MakeEpisode(feedId, durationMs: 300_000);
        data.Episodes.Add(ep);
        pc.Play(ep);

        Tick(pc, data, posMs: 270_001, lenMs: 300_000); // remain = 29 999 ms

        ep.ManuallyMarkedPlayed.Should().BeTrue();
    }

    [Fact]
    public void AutoMark_long_episode_not_marked_above_30s_remaining()
    {
        var (data, pc, feedId) = MakeSetup();
        var ep = MakeEpisode(feedId, durationMs: 300_000);
        data.Episodes.Add(ep);
        pc.Play(ep);

        // remain = 30 001 ms > 30 s, ratio = 89.9 % < 90 % → neither threshold
        Tick(pc, data, posMs: 269_999, lenMs: 300_000);

        ep.ManuallyMarkedPlayed.Should().BeFalse();
    }

    // ── auto-mark-played: short episode (≤ 60 s) ─────────────────────────────

    [Fact]
    public void AutoMark_short_episode_at_5s_remaining_threshold()
    {
        var (data, pc, feedId) = MakeSetup();
        // 60-second episode; remain = 4 999 ms < 5 s
        var ep = MakeEpisode(feedId, durationMs: 60_000);
        data.Episodes.Add(ep);
        pc.Play(ep);

        Tick(pc, data, posMs: 55_001, lenMs: 60_000); // remain = 4 999 ms

        ep.ManuallyMarkedPlayed.Should().BeTrue();
    }

    [Fact]
    public void AutoMark_short_episode_not_marked_above_5s_remaining_and_below_98_percent()
    {
        var (data, pc, feedId) = MakeSetup();
        var ep = MakeEpisode(feedId, durationMs: 60_000);
        data.Episodes.Add(ep);
        pc.Play(ep);

        // remain = 5 001 ms > 5 s, ratio ≈ 91.7 % < 98 % → neither threshold
        Tick(pc, data, posMs: 54_999, lenMs: 60_000);

        ep.ManuallyMarkedPlayed.Should().BeFalse();
    }

    // ── auto-advance ──────────────────────────────────────────────────────────

    [Fact]
    public void AutoAdvanceSuggested_fires_at_episode_end_with_next_available()
    {
        var (data, pc, feedId) = MakeSetup();
        var ep1 = MakeEpisode(feedId, durationMs: 120_000, daysAgo: 1); // current
        var ep2 = MakeEpisode(feedId, durationMs: 120_000, daysAgo: 2); // next (older)
        data.Episodes.AddRange([ep1, ep2]);
        data.AutoAdvance  = true;
        data.WrapAdvance  = false;

        Episode? suggested = null;
        pc.AutoAdvanceSuggested += ep => suggested = ep;
        pc.Play(ep1);

        // 99.5 % reached → IsEndReached returns true
        Tick(pc, data, posMs: 119_401, lenMs: 120_000);

        suggested.Should().NotBeNull();
        suggested!.Id.Should().Be(ep2.Id);
    }

    [Fact]
    public void AutoAdvanceSuggested_not_fired_when_AutoAdvance_disabled()
    {
        var (data, pc, feedId) = MakeSetup();
        var ep1 = MakeEpisode(feedId, durationMs: 120_000, daysAgo: 1);
        var ep2 = MakeEpisode(feedId, durationMs: 120_000, daysAgo: 2);
        data.Episodes.AddRange([ep1, ep2]);
        data.AutoAdvance = false;

        Episode? suggested = null;
        pc.AutoAdvanceSuggested += ep => suggested = ep;
        pc.Play(ep1);

        Tick(pc, data, posMs: 119_401, lenMs: 120_000);

        suggested.Should().BeNull();
    }
}
