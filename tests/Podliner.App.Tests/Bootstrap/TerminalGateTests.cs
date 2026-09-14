using FluentAssertions;
using Podliner.App.Bootstrap;
using Xunit;

namespace Podliner.App.Tests.Bootstrap;

// podliner crashed with an unhandled exception whenever it was started without
// a console: Terminal.Gui's WindowsDriver cannot get the console output window
// and Application.Init was never guarded. That is what the winget validator
// hit on the 2.0.0 submission, and v1.3.1 does exactly the same.
public sealed class TerminalGateTests
{
    [Fact]
    public void A_real_terminal_can_host_the_tui()
    {
        TerminalGate.CanHostTui(outputRedirected: false, errorRedirected: false).Should().BeTrue();
    }

    [Fact]
    public void Everything_redirected_means_there_is_nothing_to_draw_on()
    {
        TerminalGate.CanHostTui(outputRedirected: true, errorRedirected: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void One_stream_redirected_still_leaves_a_screen(bool output, bool error)
    {
        // `podliner > out.txt` in a terminal still has a visible screen on the
        // other stream, so it is not our business to refuse.
        TerminalGate.CanHostTui(output, error).Should().BeTrue();
    }

    [Fact]
    public void The_message_says_what_to_do_about_it()
    {
        TerminalGate.NoTerminalMessage.Should().Contain("terminal");
        TerminalGate.NoTerminalMessage.Should().Contain("--version");
    }
}
