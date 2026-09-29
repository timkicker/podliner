using FluentAssertions;
using Podliner.App.Bootstrap;
using Xunit;

namespace Podliner.App.Tests.Bootstrap;

// With no VLC, mpv or ffplay installed podliner died with an unhandled
// InvalidOperationException, exit code 134 and a stack trace in the terminal.
// That is the first thing someone sees after installing podliner-bin from the
// AUR without its optional dependencies. The message has to say what is
// missing and how to get it.
public sealed class NoAudioEngineTests
{
    [Theory]
    [InlineData("vlc")]
    [InlineData("mpv")]
    [InlineData("ffmpeg")]
    public void The_message_names_every_engine_that_would_do(string engine)
    {
        StartupMessages.NoAudioEngine.Should().Contain(engine);
    }

    [Fact]
    public void The_message_says_how_to_install_one()
    {
        StartupMessages.NoAudioEngine.Should().Contain("pacman").And.Contain("apt").And.Contain("brew");
    }

    [Fact]
    public void The_exit_code_says_it_failed()
    {
        StartupMessages.NoAudioEngineExitCode.Should().Be(1);
    }
}
