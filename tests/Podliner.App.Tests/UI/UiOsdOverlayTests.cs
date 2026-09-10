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

    // ── multi-line messages ─────────────────────────────────────────────────
    //
    // ":engine show" builds three lines: the active engine, the preference and
    // the capability list. The overlay was pinned to Height 3 and sized its
    // width from the raw string length, so it painted one line of the three
    // and made itself as wide as all of them put together.

    [Fact]
    public void Every_line_of_a_multi_line_message_reaches_the_screen()
    {
        using var tui = new TuiHarness(120, 30);
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui, "engine active: mpv\npreference: auto\nsupports: seek speed volume");

        tui.ScreenContains("engine active: mpv").Should().BeTrue(tui.Screen());
        tui.ScreenContains("preference: auto").Should().BeTrue(tui.Screen());
        tui.ScreenContains("supports: seek speed volume").Should().BeTrue(tui.Screen());
    }

    [Fact]
    public void A_multi_line_box_is_only_as_wide_as_its_longest_line()
    {
        using var tui = new TuiHarness(120, 30);
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui, "short\na much longer second line\nmid");

        var row = tui.RowOf("a much longer second line");
        row.Should().BeGreaterThan(-1);

        // The box borders sit on the rows above and below the text block.
        var top = tui.Line(row - 2);
        top.Trim().Length.Should().BeLessThan(40,
            "the width used to come from the whole string, newlines included");
    }

    [Fact]
    public void A_single_line_message_still_gets_a_three_row_box()
    {
        using var tui = new TuiHarness(120, 30);
        var shell = BuildShell();
        tui.Render();

        ShowAndPump(shell, tui, "queue: added");

        var row = tui.RowOf("queue: added");
        tui.Line(row - 1).Should().Contain("╭").And.NotContain("queue");
        tui.Line(row + 1).Should().Contain("╰");
    }
}
