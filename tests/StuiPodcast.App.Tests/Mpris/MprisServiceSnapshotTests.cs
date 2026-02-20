using FluentAssertions;
using StuiPodcast.App.Mpris;
using Xunit;

namespace StuiPodcast.App.Tests.Mpris;

public sealed class MprisServiceSnapshotTests
{
    static PlaybackSnapshot Playing(
        Guid episodeId,
        TimeSpan position,
        double speed = 1.0,
        DateTimeOffset? timestamp = null) =>
        PlaybackSnapshot.From(
            sessionId: 1,
            episodeId: episodeId,
            position: position,
            length: TimeSpan.FromSeconds(3600),
            isPlaying: true,
            speed: speed,
            now: timestamp ?? DateTimeOffset.UtcNow);

    static PlaybackSnapshot Paused(
        Guid episodeId,
        TimeSpan position,
        DateTimeOffset? timestamp = null) =>
        PlaybackSnapshot.From(
            sessionId: 1,
            episodeId: episodeId,
            position: position,
            length: TimeSpan.FromSeconds(3600),
            isPlaying: false,
            speed: 1.0,
            now: timestamp ?? DateTimeOffset.UtcNow);

    static PlaybackSnapshot Stopped() => PlaybackSnapshot.Empty;

    // ── ToPlaybackStatus ─────────────────────────────────────────────────────

    [Fact]
    public void ToPlaybackStatus_returns_Playing_when_IsPlaying()
    {
        var snap = Playing(Guid.NewGuid(), TimeSpan.FromSeconds(30));

        MprisService.ToPlaybackStatus(snap).Should().Be("Playing");
    }

    [Fact]
    public void ToPlaybackStatus_returns_Paused_when_episode_loaded_but_not_playing()
    {
        var snap = Paused(Guid.NewGuid(), TimeSpan.FromSeconds(30));

        MprisService.ToPlaybackStatus(snap).Should().Be("Paused");
    }

    [Fact]
    public void ToPlaybackStatus_returns_Stopped_when_no_episode()
    {
        MprisService.ToPlaybackStatus(Stopped()).Should().Be("Stopped");
    }

    // ── IsSeekDetected ───────────────────────────────────────────────────────

    [Fact]
    public void IsSeekDetected_returns_false_for_normal_linear_progress()
    {
        var epId = Guid.NewGuid();
        var t0   = DateTimeOffset.UtcNow;
        var prev = Playing(epId, TimeSpan.FromSeconds(100), speed: 1.0, timestamp: t0);
        // 5 s later, position advanced by 5 s → exactly linear
        var snap = Playing(epId, TimeSpan.FromSeconds(105), speed: 1.0, timestamp: t0.AddSeconds(5));

        MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }

    [Fact]
    public void IsSeekDetected_returns_true_for_large_position_jump()
    {
        var epId = Guid.NewGuid();
        var t0   = DateTimeOffset.UtcNow;
        var prev = Playing(epId, TimeSpan.FromSeconds(100), speed: 1.0, timestamp: t0);
        // 1 s later but position jumped forward by 60 s
        var snap = Playing(epId, TimeSpan.FromSeconds(161), speed: 1.0, timestamp: t0.AddSeconds(1));

        MprisService.IsSeekDetected(prev, snap).Should().BeTrue();
    }

    [Fact]
    public void IsSeekDetected_returns_false_when_not_playing()
    {
        var epId = Guid.NewGuid();
        var t0   = DateTimeOffset.UtcNow;
        var prev = Paused(epId, TimeSpan.FromSeconds(100), timestamp: t0); // not playing
        var snap = Paused(epId, TimeSpan.FromSeconds(160), timestamp: t0.AddSeconds(1));

        MprisService.IsSeekDetected(prev, snap).Should().BeFalse();
    }
}
