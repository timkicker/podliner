using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdPlaybackSeekTests
{
    private readonly FakeAudioPlayer _player = new()
    {
        State = new() { Position = TimeSpan.FromSeconds(60), Length = TimeSpan.FromSeconds(3600) }
    };

    [Fact]
    public void Seek_empty_arg_does_nothing()
    {
        CmdPlaybackModule.Seek("", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Seek_whitespace_does_nothing()
    {
        CmdPlaybackModule.Seek("   ", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Seek_absolute_seconds()
    {
        CmdPlaybackModule.Seek("120", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Seek_relative_forward()
    {
        CmdPlaybackModule.Seek("+30", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Seek_relative_backward()
    {
        CmdPlaybackModule.Seek("-20", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void Seek_percentage()
    {
        CmdPlaybackModule.Seek("50%", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(1800));
    }

    [Fact]
    public void Seek_percentage_zero()
    {
        CmdPlaybackModule.Seek("0%", _player);
        _player.State.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Seek_percentage_clamps_above_100()
    {
        CmdPlaybackModule.Seek("150%", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void Seek_mm_ss_format()
    {
        CmdPlaybackModule.Seek("5:30", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(5 * 60 + 30));
    }

    [Fact]
    public void Seek_hh_mm_ss_format()
    {
        CmdPlaybackModule.Seek("1:05:30", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(3600 + 5 * 60 + 30));
    }

    [Fact]
    public void Seek_without_seek_capability_does_nothing()
    {
        _player.Capabilities = Core.PlayerCapabilities.Play | Core.PlayerCapabilities.Pause;
        CmdPlaybackModule.Seek("+30", _player);
        _player.State.Position.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Seek_percentage_with_zero_length_does_nothing()
    {
        _player.State.Length = TimeSpan.Zero;
        var before = _player.State.Position;
        CmdPlaybackModule.Seek("50%", _player);
        _player.State.Position.Should().Be(before);
    }
}
