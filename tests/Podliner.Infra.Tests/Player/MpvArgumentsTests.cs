using FluentAssertions;
using Podliner.Infra.Player;
using Xunit;

namespace Podliner.Infra.Tests.Player;

// mpv runs as a new process for every episode, and volume and speed were
// only ever sent over its IPC socket when they changed. Every episode
// therefore started at mpv's own 100% volume and 1.0x, whatever podliner
// showed and had saved (measured over the socket: 7% and 1.5x saved, 100%
// and 1.0x playing).
public sealed class MpvArgumentsTests
{
    [Fact]
    public void A_new_episode_starts_at_the_current_volume_and_speed()
    {
        var args = MpvAudioPlayer.BuildArguments("https://ex.test/a.mp3", "/tmp/s.sock", null, volume0to100: 7, speed: 1.5);

        args.Should().Contain("--volume=7");
        args.Should().Contain("--speed=1.5");
        args.Last().Should().Be("https://ex.test/a.mp3");
    }

    [Fact]
    public void The_speed_is_written_with_a_dot_whatever_the_locale()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-AT");
            MpvAudioPlayer.BuildArguments("u", "s", 90_500, 50, 1.25)
                .Should().Contain("--speed=1.25").And.Contain("--start=90.5");
        }
        finally { Thread.CurrentThread.CurrentCulture = before; }
    }
}
