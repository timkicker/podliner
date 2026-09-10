using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
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
    private readonly TransportUseCase _sut;

    public CmdPlaybackVolumeSpeedTests()
    {
        _sut = new TransportUseCase(_player, _ui, _data, () => Task.CompletedTask, new FakeEpisodeStore());
    }

    // --- Volume ---

    [Fact]
    public void Volume_absolute_sets_value()
    {
        _sut.Volume("80");
        _player.State.Volume0_100.Should().Be(80);
        _data.Volume0_100.Should().Be(80);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("80"));
    }

    [Fact]
    public void Volume_relative_plus()
    {
        _sut.Volume("+10");
        _player.State.Volume0_100.Should().Be(60);
        _data.Volume0_100.Should().Be(60);
    }

    [Fact]
    public void Volume_relative_minus()
    {
        _sut.Volume("-20");
        _player.State.Volume0_100.Should().Be(30);
    }

    [Fact]
    public void Volume_clamps_to_0()
    {
        _sut.Volume("-999");
        _player.State.Volume0_100.Should().Be(0);
    }

    [Fact]
    public void Volume_clamps_to_100()
    {
        _sut.Volume("999");
        _player.State.Volume0_100.Should().Be(100);
    }

    [Fact]
    public void Volume_no_capability_shows_error()
    {
        _player.Capabilities = PlayerCapabilities.Play;
        _sut.Volume("50");
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("not supported"));
    }

    [Fact]
    public void Volume_empty_arg_does_nothing()
    {
        _sut.Volume("");
        _player.State.Volume0_100.Should().Be(50);
    }

    // --- Speed ---

    [Fact]
    public void Speed_absolute_sets_value()
    {
        _sut.Speed("1.5");
        _player.State.Speed.Should().Be(1.5);
        _data.Speed.Should().Be(1.5);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("1.5"));
    }

    [Fact]
    public void Speed_relative_plus()
    {
        _sut.Speed("+0.5");
        _player.State.Speed.Should().Be(1.5);
    }

    [Fact]
    public void Speed_relative_minus()
    {
        _sut.Speed("-0.5");
        _player.State.Speed.Should().Be(0.5);
    }

    [Fact]
    public void Speed_clamps_to_025()
    {
        _sut.Speed("0.1");
        _player.State.Speed.Should().Be(0.25);
    }

    [Fact]
    public void Speed_clamps_to_3()
    {
        _sut.Speed("5.0");
        _player.State.Speed.Should().Be(3.0);
    }

    [Fact]
    public void Speed_no_capability_shows_error()
    {
        _player.Capabilities = PlayerCapabilities.Play;
        _sut.Speed("1.5");
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

        _sut.Speed(arg);

        _player.State.Speed.Should().Be(originalSpeed);
        _data.Speed.Should().Be(originalData);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("not supported"));
    }

    [Fact]
    public void Speed_comma_as_decimal_separator()
    {
        _sut.Speed("1,5");
        _player.State.Speed.Should().Be(1.5);
    }

    [Fact]
    public void Speed_empty_arg_does_nothing()
    {
        _sut.Speed("");
        _player.State.Speed.Should().Be(1.0);
    }

    // ── speed never drifts ───────────────────────────────────────────────────

    [Fact]
    public void Stepping_up_and_down_returns_to_exactly_one()
    {
        // ":speed +0.1" ten times then "-0.1" ten times used to leave
        // 0.9999999999999997 in appsettings.json.
        for (int i = 0; i < 10; i++) _sut.Speed("+0.1");
        for (int i = 0; i < 10; i++) _sut.Speed("-0.1");

        _data.Speed.Should().Be(1.0);
    }

    [Fact]
    public void A_single_step_lands_on_a_clean_value()
    {
        _sut.Speed("+0.1");

        _data.Speed.Should().Be(1.1);
    }

    [Theory]
    [InlineData(0.9999999999999997, 1.0)]
    [InlineData(1.2500000000000002, 1.25)]
    [InlineData(2.9999999999, 3.0)]
    public void Normalising_snaps_to_two_decimals(double raw, double expected)
        => TransportUseCase.NormalizeSpeed(raw).Should().Be(expected);

    [Theory]
    [InlineData(-5.0, 0.25)]
    [InlineData(0.1, 0.25)]
    [InlineData(99.0, 3.0)]
    public void Normalising_clamps_to_the_supported_range(double raw, double expected)
        => TransportUseCase.NormalizeSpeed(raw).Should().Be(expected);

    [Fact]
    public void Every_reachable_speed_survives_a_round_trip_through_the_config()
    {
        // Whatever stepping produces has to be a value ConfigStore keeps.
        for (double s = 0.25; s <= 3.0; s += 0.05)
        {
            var n = TransportUseCase.NormalizeSpeed(s);
            n.Should().Be(Math.Round(n, 2));
        }
    }
}
