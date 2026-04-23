using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdPlaybackSeekTests
{
    private readonly FakeAudioPlayer _player = new()
    {
        State = new() { Position = TimeSpan.FromSeconds(60), Length = TimeSpan.FromSeconds(3600) }
    };
    private readonly FakeUiShell _ui = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly AppData _data = new();
    private readonly TransportUseCase _sut;

    public CmdPlaybackSeekTests()
    {
        _sut = new TransportUseCase(_player, _ui, _data, () => Task.CompletedTask, _episodes);
    }

    [Fact]
    public void Seek_empty_arg_does_nothing()
    {
        _sut.Seek("");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Seek_whitespace_does_nothing()
    {
        _sut.Seek("   ");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Seek_absolute_seconds()
    {
        _sut.Seek("120");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Seek_relative_forward()
    {
        _sut.Seek("+30");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Seek_relative_backward()
    {
        _sut.Seek("-20");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void Seek_percentage()
    {
        _sut.Seek("50%");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(1800));
    }

    [Fact]
    public void Seek_percentage_zero()
    {
        _sut.Seek("0%");
        _player.State.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Seek_percentage_clamps_above_100()
    {
        _sut.Seek("150%");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void Seek_mm_ss_format()
    {
        _sut.Seek("5:30");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(5 * 60 + 30));
    }

    [Fact]
    public void Seek_hh_mm_ss_format()
    {
        _sut.Seek("1:05:30");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(3600 + 5 * 60 + 30));
    }

    [Fact]
    public void Seek_without_seek_capability_does_nothing()
    {
        _player.Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Pause;
        _sut.Seek("+30");
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Seek_percentage_with_zero_length_does_nothing()
    {
        _player.State.Length = TimeSpan.Zero;
        var before = _player.State.Position;
        _sut.Seek("50%");
        _player.State.Position.Should().Be(before);
    }
}
