using FluentAssertions;
using Podliner.App.Services;
using Xunit;

namespace Podliner.App.Tests.Services;

// ":copy" said "copied" and left the clipboard untouched on Wayland: the only
// Linux candidates were xclip and xsel, and xclip exits 0 on a Wayland session
// without owning the clipboard, so nothing ever reported a failure.
public sealed class ClipboardCommandTests
{
    private static IReadOnlyList<string> Tools(bool win = false, bool mac = false, bool wayland = false)
        => ClipboardCommand.Candidates(win, mac, wayland, "hello").Select(c => c.File).ToList();

    [Fact]
    public void A_wayland_session_reaches_for_wl_copy_first()
    {
        Tools(wayland: true).Should().StartWith(new[] { "wl-copy" });
    }

    [Fact]
    public void An_x_session_reaches_for_xclip_first()
    {
        Tools(wayland: false).Should().StartWith(new[] { "xclip" });
    }

    [Fact]
    public void Every_linux_tool_stays_available_as_a_fallback()
    {
        Tools(wayland: true).Should().BeEquivalentTo(new[] { "wl-copy", "xclip", "xsel" });
        Tools(wayland: false).Should().BeEquivalentTo(new[] { "wl-copy", "xclip", "xsel" });
    }

    [Fact]
    public void Windows_and_mac_have_exactly_one_tool_each()
    {
        Tools(win: true).Should().Equal("powershell");
        Tools(mac: true).Should().Equal("pbcopy");
    }

    [Fact]
    public void Only_powershell_takes_the_text_as_an_argument()
    {
        var win = ClipboardCommand.Candidates(true, false, false, "hello").Single();
        win.Arguments.Should().Contain("hello");
        ClipboardCommand.WritesToStdin(win).Should().BeFalse();

        foreach (var c in ClipboardCommand.Candidates(false, false, true, "hello"))
        {
            c.Arguments.Should().NotContain("hello");
            ClipboardCommand.WritesToStdin(c).Should().BeTrue();
        }
    }
}
