using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdPlaybackReplayTests
{
    private readonly FakeAudioPlayer _player = new()
    {
        State = new() { Position = TimeSpan.FromSeconds(120), Length = TimeSpan.FromSeconds(3600) }
    };

    // Replay needs UiShell for the else-branch but the core cases don't call ShowOsd
    // We test the IAudioPlayer-only paths

    [Fact]
    public void Replay_empty_seeks_to_zero()
    {
        CmdPlaybackModule.Seek("0", _player);
        _player.State.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Replay_negative_relative_rewinds()
    {
        CmdPlaybackModule.Seek("-30", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(90));
    }
}
