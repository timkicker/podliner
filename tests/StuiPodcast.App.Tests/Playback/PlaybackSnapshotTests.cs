using FluentAssertions;
using Xunit;

namespace StuiPodcast.App.Tests.Playback;

public sealed class PlaybackSnapshotTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;
    static readonly Guid EpId = Guid.NewGuid();

    [Fact]
    public void From_clamps_negative_position_to_zero()
    {
        var snap = PlaybackSnapshot.From(1, EpId, TimeSpan.FromSeconds(-10), TimeSpan.FromSeconds(60), false, 1.0, T0);

        snap.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void From_clamps_negative_length_to_zero()
    {
        var snap = PlaybackSnapshot.From(1, EpId, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(-30), false, 1.0, T0);

        snap.Length.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void From_defaults_zero_speed_to_one()
    {
        var snap = PlaybackSnapshot.From(1, EpId, TimeSpan.Zero, TimeSpan.Zero, false, speed: 0.0, now: T0);

        snap.Speed.Should().Be(1.0);
    }

    [Fact]
    public void From_defaults_negative_speed_to_one()
    {
        var snap = PlaybackSnapshot.From(1, EpId, TimeSpan.Zero, TimeSpan.Zero, false, speed: -2.0, now: T0);

        snap.Speed.Should().Be(1.0);
    }

    [Fact]
    public void From_preserves_valid_values()
    {
        var pos    = TimeSpan.FromSeconds(45);
        var length = TimeSpan.FromMinutes(30);
        var snap   = PlaybackSnapshot.From(7, EpId, pos, length, isPlaying: true, speed: 1.5, now: T0);

        snap.SessionId.Should().Be(7);
        snap.EpisodeId.Should().Be(EpId);
        snap.Position.Should().Be(pos);
        snap.Length.Should().Be(length);
        snap.IsPlaying.Should().BeTrue();
        snap.Speed.Should().Be(1.5);
        snap.Timestamp.Should().Be(T0);
    }
}
