using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdPlaybackReplayTests
{
    private readonly FakeAudioPlayer _player = new()
    {
        State = new() { Position = TimeSpan.FromSeconds(120), Length = TimeSpan.FromSeconds(3600) }
    };
    private readonly TransportUseCase _sut;

    public CmdPlaybackReplayTests()
    {
        _sut = new TransportUseCase(_player, new FakeUiShell(), new AppData(), () => Task.CompletedTask, new FakeEpisodeStore());
    }

    [Fact]
    public void Replay_empty_seeks_to_zero()
    {
        _sut.Seek("0");
        _player.State.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Replay_negative_relative_rewinds()
    {
        _sut.Seek("-30");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(90));
    }
}
