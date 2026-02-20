using FluentAssertions;
using Xunit;

namespace StuiPodcast.App.Tests.Mpris;

public sealed class MprisServiceSnapshotEdgeCaseTests
{
    static readonly Guid EpId = Guid.NewGuid();
    static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    static PlaybackSnapshot Playing(Guid episodeId, double posSeconds, double speedFactor = 1.0, DateTimeOffset? at = null)
        => PlaybackSnapshot.From(
            1, episodeId,
            TimeSpan.FromSeconds(posSeconds),
            TimeSpan.FromMinutes(30),
            isPlaying: true,
            speed: speedFactor,
            now: at ?? T0);

    static PlaybackSnapshot Stopped()
        => PlaybackSnapshot.From(1, null, TimeSpan.Zero, TimeSpan.Zero, false, 1.0, T0);

    // ── Episode change edge cases ─────────────────────────────────────────────

    [Fact]
    public void IsSeekDetected_returns_false_when_episode_changes()
    {
        var prev = Playing(EpId,       posSeconds: 60, at: T0);
        var snap = Playing(Guid.NewGuid(), posSeconds: 5, at: T0.AddSeconds(1));

        // Different EpisodeId → guard returns false even though position jumped
        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }

    [Fact]
    public void IsSeekDetected_returns_false_when_prev_episode_is_null()
    {
        var prev = Stopped(); // EpisodeId = null, IsPlaying = false
        var snap = Playing(EpId, posSeconds: 30, at: T0.AddSeconds(1));

        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }

    // ── Elapsed time boundary ─────────────────────────────────────────────────

    [Fact]
    public void IsSeekDetected_returns_false_when_elapsed_is_zero()
    {
        // Same timestamp → elapsed = 0, guard fires
        var prev = Playing(EpId, posSeconds: 30, at: T0);
        var snap = Playing(EpId, posSeconds: 90, at: T0); // same timestamp

        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }

    [Fact]
    public void IsSeekDetected_returns_false_when_elapsed_exceeds_10_seconds()
    {
        // Timer gap > 10s → guard treats it as a legitimate long pause, not a seek
        var prev = Playing(EpId, posSeconds: 30,  at: T0);
        var snap = Playing(EpId, posSeconds: 100, at: T0.AddSeconds(11));

        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }

    [Fact]
    public void IsSeekDetected_returns_false_when_elapsed_is_negative()
    {
        // Timestamp went backwards (clock skew) → guard fires
        var prev = Playing(EpId, posSeconds: 60, at: T0);
        var snap = Playing(EpId, posSeconds: 90, at: T0.AddSeconds(-1));

        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }

    // ── Speed factor ──────────────────────────────────────────────────────────

    [Fact]
    public void IsSeekDetected_accounts_for_2x_speed_in_expected_position()
    {
        // At 2x speed, 5 real seconds → 10 audio seconds of progress
        // prev.Position = 30s, elapsed = 5s, speed = 2.0 → expected = 40s
        // snap.Position = 41s → diff ≈ 1s < 2s → NOT a seek
        var prev = Playing(EpId, posSeconds: 30, speedFactor: 2.0, at: T0);
        var snap = Playing(EpId, posSeconds: 41, speedFactor: 2.0, at: T0.AddSeconds(5));

        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }

    [Fact]
    public void IsSeekDetected_detects_seek_even_at_2x_speed()
    {
        // At 2x speed, 5 real seconds → expected = 40s; snap = 100s → diff = 60s > 2s → seek
        var prev = Playing(EpId, posSeconds: 30, speedFactor: 2.0, at: T0);
        var snap = Playing(EpId, posSeconds: 100, speedFactor: 2.0, at: T0.AddSeconds(5));

        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeTrue();
    }

    // ── Backward seek ─────────────────────────────────────────────────────────

    [Fact]
    public void IsSeekDetected_returns_true_for_backward_seek()
    {
        // Normal: expected position ≈ 31s, but user jumped back to 5s → diff = 26s > 2s
        var prev = Playing(EpId, posSeconds: 30, at: T0);
        var snap = Playing(EpId, posSeconds: 5,  at: T0.AddSeconds(1));

        StuiPodcast.App.Mpris.MprisService.IsSeekDetected(prev, snap).Should().BeTrue();
    }
}
