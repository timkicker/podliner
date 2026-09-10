using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.UI;
using Terminal.Gui;
using Xunit;

namespace Podliner.App.Tests.UI;

// The OSD is how podliner answers almost every command. It is also the piece
// with the nastiest historical failure mode: if it gets parented into a
// dialog's subview tree instead of the stable root Toplevel it is orphaned
// when the dialog closes and messages silently stop appearing.
[Collection(TuiCollection.Name)]
public sealed class UiOsdOverlayTests
{
    private static UiShell BuildShell()
    {
        var shell = new UiShell(new MemoryLogSink());
        shell.Build();
        return shell;
    }

    private static void ShowAndPump(UiShell shell, TuiHarness tui, string text, int ms = 5000)
    {
        shell.ShowOsd(text, ms);
        tui.Pump();
        tui.Render();
    }

    [Fact]
    public void A_message_reaches_the_screen()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui, "queue: added");

        tui.ScreenContains("queue: added").Should().BeTrue();
    }

    [Fact]
    public void A_later_message_replaces_the_earlier_one()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui, "first message");
        ShowAndPump(shell, tui, "second message");

        tui.ScreenContains("second message").Should().BeTrue();
        tui.ScreenContains("first message").Should().BeFalse();
    }

    [Fact]
    public void An_empty_message_does_not_throw()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        var act = () => ShowAndPump(shell, tui, "");

        act.Should().NotThrow();
    }

    [Fact]
    public void A_long_message_stays_inside_the_screen()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui,
            "sync: HTTP 404 Not Found — this looks like a Nextcloud server, try the /index.php path");

        tui.Lines().Should().OnlyContain(l => l.Length <= tui.Cols);
    }

    [Fact]
    public void A_message_survives_a_resize()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();
        ShowAndPump(shell, tui, "still here");

        tui.Resize(120, 40);

        tui.ScreenContains("still here").Should().BeTrue();
    }

    [Fact]
    public void Messages_still_appear_after_a_forced_redraw()
    {
        // ForceRedraw re-lays-out the root Toplevel; the overlay is parented
        // there and must not be dropped in the process.
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        shell.ForceRedraw();
        tui.Pump();
        ShowAndPump(shell, tui, "after redraw");

        tui.ScreenContains("after redraw").Should().BeTrue();
    }

    [Fact]
    public void Many_messages_in_a_row_do_not_stack_up()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        for (int i = 0; i < 20; i++) ShowAndPump(shell, tui, $"msg {i}");

        tui.ScreenContains("msg 19").Should().BeTrue();
        tui.ScreenContains("msg 18").Should().BeFalse();
        tui.ScreenContains("msg 0").Should().BeFalse();
    }

    [Fact]
    public void A_message_does_not_cover_the_player_panel()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui, "osd text");

        tui.ScreenContains("AudioPlayer").Should().BeTrue();
    }

    [Fact]
    public void Unicode_in_a_message_renders()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui, "💤 sleep timer: playback stopped");

        tui.ScreenContains("sleep timer").Should().BeTrue();
    }
}
