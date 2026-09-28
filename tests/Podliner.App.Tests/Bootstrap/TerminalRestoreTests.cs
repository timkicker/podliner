using FluentAssertions;
using Podliner.App.Bootstrap;
using Xunit;

namespace Podliner.App.Tests.Bootstrap;

// Issue #34: podliner switched on mouse tracking and the alternate screen and
// left both on after quitting, so the shell printed mouse reports as text.
// TerminalRestore is what switches them off, and it has to work from any
// thread at any point of a dying process.
public sealed class TerminalRestoreTests
{
    [Theory]
    [InlineData("?1000")]
    [InlineData("?1002")]
    [InlineData("?1003")]
    [InlineData("?1006")]
    [InlineData("?1015")]
    [InlineData("?1049")]
    [InlineData("?2004")]
    public void Every_mode_podliner_can_switch_on_is_switched_off(string mode)
    {
        var w = new StringWriter();

        TerminalRestore.Restore(w);

        w.ToString().Should().Contain($"\x1b[{mode}l");
    }

    [Fact]
    public void The_cursor_comes_back()
    {
        var w = new StringWriter();

        TerminalRestore.Restore(w);

        w.ToString().Should().Contain("\x1b[?25h");
    }

    [Fact]
    public void Leaving_the_alternate_screen_comes_last()
    {
        // Anything written after ?1049l lands on the user's shell screen.
        var w = new StringWriter();

        TerminalRestore.Restore(w);

        w.ToString().Should().EndWith("\x1b[?1049l");
    }

    [Fact]
    public void A_broken_writer_does_not_throw()
    {
        // It runs from ProcessExit and a watchdog; an exception there is lost
        // at best and kills the rest of the cleanup at worst.
        var act = () => TerminalRestore.Restore(new ThrowingWriter());

        act.Should().NotThrow();
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => throw new IOException("closed");
        public override void Write(string? value) => throw new IOException("closed");
        public override void Flush() => throw new IOException("closed");
    }
}
