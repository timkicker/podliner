using FluentAssertions;
using StuiPodcast.App;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Playback;

public sealed class SwappablePlayerTests
{
    [Fact]
    public void Forwards_Name_from_inner()
    {
        var inner = new FakeAudioPlayer();
        using var sw = new SwappableAudioPlayer(inner);

        sw.Name.Should().Be(inner.Name);
    }

    [Fact]
    public void Forwards_Capabilities_from_inner()
    {
        var inner = new FakeAudioPlayer
        {
            Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Seek
        };
        using var sw = new SwappableAudioPlayer(inner);

        sw.Capabilities.Should().Be(inner.Capabilities);
    }

    [Fact]
    public void Forwards_State_from_inner()
    {
        var inner = new FakeAudioPlayer { State = new PlayerState { Volume0_100 = 42, Speed = 1.5 } };
        using var sw = new SwappableAudioPlayer(inner);

        sw.State.Volume0_100.Should().Be(42);
        sw.State.Speed.Should().Be(1.5);
    }

    [Fact]
    public void Forwards_SetVolume_to_inner()
    {
        var inner = new FakeAudioPlayer();
        using var sw = new SwappableAudioPlayer(inner);

        sw.SetVolume(75);

        inner.LastSetVolume.Should().Be(75);
        inner.State.Volume0_100.Should().Be(75);
    }

    [Fact]
    public void Forwards_SetSpeed_to_inner()
    {
        var inner = new FakeAudioPlayer();
        using var sw = new SwappableAudioPlayer(inner);

        sw.SetSpeed(1.75);

        inner.LastSetSpeed.Should().Be(1.75);
    }

    [Fact]
    public void Forwards_TogglePause_to_inner()
    {
        var inner = new FakeAudioPlayer { State = new PlayerState { IsPlaying = true } };
        using var sw = new SwappableAudioPlayer(inner);

        sw.TogglePause();

        inner.State.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public void Forwards_SeekTo_to_inner()
    {
        var inner = new FakeAudioPlayer();
        using var sw = new SwappableAudioPlayer(inner);

        sw.SeekTo(TimeSpan.FromSeconds(123));

        inner.State.Position.Should().Be(TimeSpan.FromSeconds(123));
    }

    [Fact]
    public async Task SwapToAsync_replaces_inner_player()
    {
        var first = new FakeAudioPlayer();
        var second = new FakeAudioPlayer();
        using var sw = new SwappableAudioPlayer(first);

        await sw.SwapToAsync(second);

        // Now calls should go to `second`, not `first`.
        sw.SetVolume(33);
        second.LastSetVolume.Should().Be(33);
        first.LastSetVolume.Should().NotBe(33, "old inner must no longer receive calls");
    }

    [Fact]
    public async Task SwapToAsync_disposes_old_inner()
    {
        var first = new TrackingFakePlayer();
        var second = new FakeAudioPlayer();
        var sw = new SwappableAudioPlayer(first);

        await sw.SwapToAsync(second);

        first.DisposedCount.Should().Be(1, "old inner must be disposed on swap");
    }

    [Fact]
    public async Task SwapToAsync_calls_onBeforeDispose_hook()
    {
        var first = new FakeAudioPlayer();
        var second = new FakeAudioPlayer();
        using var sw = new SwappableAudioPlayer(first);

        IAudioPlayerLike? received = null;
        await sw.SwapToAsync(second, old =>
        {
            received = new IAudioPlayerLike(old);
        });

        received.Should().NotBeNull();
        received!.Name.Should().Be(first.Name);
    }

    [Fact]
    public void Dispose_unsubscribes_and_disposes_inner()
    {
        var inner = new TrackingFakePlayer();
        var sw = new SwappableAudioPlayer(inner);

        sw.Dispose();

        inner.DisposedCount.Should().Be(1);
    }

    [Fact]
    public async Task SwapToAsync_throws_on_null_next()
    {
        using var sw = new SwappableAudioPlayer(new FakeAudioPlayer());

        var act = () => sw.SwapToAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // Helper types
    private sealed class IAudioPlayerLike
    {
        public string Name { get; }
        public IAudioPlayerLike(Infra.Player.IAudioPlayer p) { Name = p.Name; }
    }

    private sealed class TrackingFakePlayer : Infra.Player.IAudioPlayer
    {
        public event Action<PlayerState>? StateChanged;
        public PlayerState State { get; } = new();
        public string Name => "Tracking";
        public PlayerCapabilities Capabilities { get; } = PlayerCapabilities.Play;
        public int DisposedCount { get; private set; }

        public void Play(string url, long? startMs = null) { }
        public void TogglePause() { }
        public void SeekRelative(TimeSpan d) { }
        public void SeekTo(TimeSpan p) { }
        public void SetVolume(int v) { }
        public void SetSpeed(double s) { }
        public void Stop() { }
        public void Dispose() { DisposedCount++; }

        // avoid "never used" warning
        private void _Unused() => StateChanged?.Invoke(State);
    }
}
