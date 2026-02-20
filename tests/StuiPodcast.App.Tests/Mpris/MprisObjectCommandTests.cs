using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Mpris;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Tmds.DBus;
using Xunit;

namespace StuiPodcast.App.Tests.Mpris;

public sealed class MprisObjectCommandTests
{
    static (MprisObject obj, FakeAudioPlayer player, AppData data) MakeObject()
    {
        var data   = new AppData();
        var player = new FakeAudioPlayer();
        var pc     = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink());
        return (new MprisObject(data, player, pc), player, data);
    }

    // ── PauseAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PauseAsync_toggles_pause_when_currently_playing()
    {
        var (obj, player, _) = MakeObject();
        player.State.IsPlaying = true;

        await ((IMprisPlayer)obj).PauseAsync();

        player.State.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public async Task PauseAsync_does_nothing_when_already_paused()
    {
        var (obj, player, _) = MakeObject();
        player.State.IsPlaying = false;

        await ((IMprisPlayer)obj).PauseAsync();

        // TogglePause was not called, state stays false
        player.State.IsPlaying.Should().BeFalse();
    }

    // ── PlayAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayAsync_toggles_when_currently_paused()
    {
        var (obj, player, _) = MakeObject();
        player.State.IsPlaying = false;

        await ((IMprisPlayer)obj).PlayAsync();

        player.State.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public async Task PlayAsync_does_nothing_when_already_playing()
    {
        var (obj, player, _) = MakeObject();
        player.State.IsPlaying = true;

        await ((IMprisPlayer)obj).PlayAsync();

        // TogglePause was not called, state stays true
        player.State.IsPlaying.Should().BeTrue();
    }

    // ── PlayPauseAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayPauseAsync_toggles_from_playing_to_paused()
    {
        var (obj, player, _) = MakeObject();
        player.State.IsPlaying = true;

        await ((IMprisPlayer)obj).PlayPauseAsync();

        player.State.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public async Task PlayPauseAsync_toggles_from_paused_to_playing()
    {
        var (obj, player, _) = MakeObject();
        player.State.IsPlaying = false;

        await ((IMprisPlayer)obj).PlayPauseAsync();

        player.State.IsPlaying.Should().BeTrue();
    }

    // ── StopAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_stops_playback_and_clears_episode()
    {
        var (obj, player, _) = MakeObject();
        player.State.IsPlaying = true;
        player.State.EpisodeId = Guid.NewGuid();

        await ((IMprisPlayer)obj).StopAsync();

        player.State.IsPlaying.Should().BeFalse();
        player.State.EpisodeId.Should().BeNull();
    }

    // ── SeekAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeekAsync_calls_SeekRelative_with_microseconds_converted_to_timespan()
    {
        var (obj, player, _) = MakeObject();
        player.State.Position = TimeSpan.FromSeconds(30);

        // 10 seconds in microseconds
        await ((IMprisPlayer)obj).SeekAsync(10_000_000L);

        player.State.Position.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task SeekAsync_with_negative_offset_seeks_backward()
    {
        var (obj, player, _) = MakeObject();
        player.State.Position = TimeSpan.FromSeconds(30);

        // -5 seconds in microseconds
        await ((IMprisPlayer)obj).SeekAsync(-5_000_000L);

        player.State.Position.Should().Be(TimeSpan.FromSeconds(25));
    }

    // ── SetPositionAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetPositionAsync_seeks_to_absolute_microsecond_position()
    {
        var (obj, player, _) = MakeObject();
        player.State.Position = TimeSpan.FromSeconds(10);

        // 60 seconds in microseconds
        await ((IMprisPlayer)obj).SetPositionAsync(new ObjectPath("/some/track"), 60_000_000L);

        player.State.Position.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task SetPositionAsync_to_zero_seeks_to_start()
    {
        var (obj, player, _) = MakeObject();
        player.State.Position = TimeSpan.FromMinutes(5);

        await ((IMprisPlayer)obj).SetPositionAsync(new ObjectPath("/some/track"), 0L);

        player.State.Position.Should().Be(TimeSpan.Zero);
    }
}
