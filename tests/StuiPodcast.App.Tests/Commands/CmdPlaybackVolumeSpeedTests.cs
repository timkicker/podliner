using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdPlaybackVolumeSpeedTests
{
    private readonly FakeAudioPlayer _player = new()
    {
        State = new() { Volume0_100 = 50, Speed = 1.0 }
    };
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new() { Volume0_100 = 50, Speed = 1.0 };
    private Task SaveAsync() => Task.CompletedTask;

    // --- Volume ---

    [Fact]
    public void Volume_absolute_sets_value()
    {
        CmdPlaybackModule.Volume("80", _player, _data, SaveAsync, _ui);
        _player.State.Volume0_100.Should().Be(80);
        _data.Volume0_100.Should().Be(80);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("80"));
    }

    [Fact]
    public void Volume_relative_plus()
    {
        CmdPlaybackModule.Volume("+10", _player, _data, SaveAsync, _ui);
        _player.State.Volume0_100.Should().Be(60);
        _data.Volume0_100.Should().Be(60);
    }

    [Fact]
    public void Volume_relative_minus()
    {
        CmdPlaybackModule.Volume("-20", _player, _data, SaveAsync, _ui);
        _player.State.Volume0_100.Should().Be(30);
    }

    [Fact]
    public void Volume_clamps_to_0()
    {
        CmdPlaybackModule.Volume("-999", _player, _data, SaveAsync, _ui);
        _player.State.Volume0_100.Should().Be(0);
    }

    [Fact]
    public void Volume_clamps_to_100()
    {
        CmdPlaybackModule.Volume("999", _player, _data, SaveAsync, _ui);
        _player.State.Volume0_100.Should().Be(100);
    }

    [Fact]
    public void Volume_no_capability_shows_error()
    {
        _player.Capabilities = PlayerCapabilities.Play;
        CmdPlaybackModule.Volume("50", _player, _data, SaveAsync, _ui);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("not supported"));
    }

    [Fact]
    public void Volume_empty_arg_does_nothing()
    {
        CmdPlaybackModule.Volume("", _player, _data, SaveAsync, _ui);
        _player.State.Volume0_100.Should().Be(50);
    }

    // --- Speed ---

    [Fact]
    public void Speed_absolute_sets_value()
    {
        CmdPlaybackModule.Speed("1.5", _player, _data, SaveAsync, _ui);
        _player.State.Speed.Should().Be(1.5);
        _data.Speed.Should().Be(1.5);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("1.5"));
    }

    [Fact]
    public void Speed_relative_plus()
    {
        CmdPlaybackModule.Speed("+0.5", _player, _data, SaveAsync, _ui);
        _player.State.Speed.Should().Be(1.5);
    }

    [Fact]
    public void Speed_relative_minus()
    {
        CmdPlaybackModule.Speed("-0.5", _player, _data, SaveAsync, _ui);
        _player.State.Speed.Should().Be(0.5);
    }

    [Fact]
    public void Speed_clamps_to_025()
    {
        CmdPlaybackModule.Speed("0.1", _player, _data, SaveAsync, _ui);
        _player.State.Speed.Should().Be(0.25);
    }

    [Fact]
    public void Speed_clamps_to_3()
    {
        CmdPlaybackModule.Speed("5.0", _player, _data, SaveAsync, _ui);
        _player.State.Speed.Should().Be(3.0);
    }

    [Fact]
    public void Speed_no_capability_shows_error()
    {
        _player.Capabilities = PlayerCapabilities.Play;
        CmdPlaybackModule.Speed("1.5", _player, _data, SaveAsync, _ui);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("not supported"));
    }

    // Regression for issue #18: MediaFoundation-like capabilities (no Speed flag) must
    // reject all speed changes — presets and relative/absolute — and never touch player state.
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.25")]
    [InlineData("1.5")]
    [InlineData("+0.1")]
    [InlineData("-0.1")]
    public void Speed_without_capability_rejects_all_forms(string arg)
    {
        _player.Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Pause
            | PlayerCapabilities.Stop | PlayerCapabilities.Seek | PlayerCapabilities.Volume;
        var originalSpeed = _player.State.Speed;
        var originalData = _data.Speed;

        CmdPlaybackModule.Speed(arg, _player, _data, SaveAsync, _ui);

        _player.State.Speed.Should().Be(originalSpeed);
        _data.Speed.Should().Be(originalData);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("not supported"));
    }

    [Fact]
    public void Speed_comma_as_decimal_separator()
    {
        CmdPlaybackModule.Speed("1,5", _player, _data, SaveAsync, _ui);
        _player.State.Speed.Should().Be(1.5);
    }

    [Fact]
    public void Speed_empty_arg_does_nothing()
    {
        CmdPlaybackModule.Speed("", _player, _data, SaveAsync, _ui);
        _player.State.Speed.Should().Be(1.0);
    }
}
