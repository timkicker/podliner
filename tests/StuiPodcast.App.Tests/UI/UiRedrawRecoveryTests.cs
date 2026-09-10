using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using Terminal.Gui;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// Issue #4: the layout goes crooked after the terminal window is moved or
// resized and the only known cure is restarting podliner.
//
// Root cause found by disassembling Terminal.Gui 1.19: resize detection is
// polling, not signal-driven. CursesDriver.ProcessWinChange asks
// Curses.CheckWinChange, which compares ncurses' LINES/COLS globals against a
// cached copy, and it only runs from CursesDriver.Refresh and the input loop.
// When that poll is missed, Application.Driver knows the new size but every
// Toplevel keeps its old Frame, and each view renders at the stale geometry.
//
// These tests reproduce that desync and prove it is recoverable through
// public Terminal.Gui API, so podliner does not have to be restarted.
[Collection(TuiCollection.Name)]
public sealed class UiRedrawRecoveryTests
{
    private static readonly Guid FeedId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static UiShell BuildShell()
    {
        var shell = new UiShell(new MemoryLogSink());
        shell.Build();
        shell.SetFeeds(new[] { new Feed { Id = FeedId, Title = "Feed", Url = "https://ex.test/f.xml" } });
        shell.SetEpisodesForFeed(FeedId, new[]
        {
            new Episode
            {
                Id = Guid.NewGuid(), FeedId = FeedId, Title = "Episode",
                AudioUrl = "https://ex.test/a.mp3",
                PubDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            }
        });
        return shell;
    }

    // Reproduces a missed resize poll. FakeDriver.SetWindowSize propagates
    // the new size to the Toplevel straight away, which is what a *working*
    // poll does; CursesDriver only gets there via ProcessWinChange. Putting
    // the Toplevel back to its previous frame leaves exactly the state a
    // missed poll produces: driver resized, layout stale.
    private static void SimulateMissedResizePoll(TuiHarness tui, int cols, int rows)
    {
        var stale = Application.Top.Frame;
        tui.Driver.SetWindowSize(cols, rows);
        Application.Top.Frame = stale;
    }

    // UiShell marshals every mutation through the main loop, ForceRedraw
    // included, so the queue has to be drained before asserting.
    private static void Redraw(UiShell shell, TuiHarness tui)
    {
        shell.ForceRedraw();
        tui.Pump();
    }

    [Fact]
    public void A_missed_resize_poll_leaves_the_toplevel_at_the_old_size()
    {
        using var tui = new TuiHarness();
        BuildShell();
        tui.Render();

        SimulateMissedResizePoll(tui, 120, 40);

        // This is the broken state users see: driver says 120, layout says 80.
        Application.Driver.Cols.Should().Be(120);
        Application.Top.Frame.Width.Should().Be(80);
    }

    [Fact]
    public void ForceRedraw_reconciles_the_toplevel_with_the_driver()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();
        SimulateMissedResizePoll(tui, 120, 40);

        Redraw(shell, tui);

        Application.Top.Frame.Width.Should().Be(120);
        Application.Top.Frame.Height.Should().Be(40);
    }

    [Fact]
    public void ForceRedraw_restores_a_usable_screen_after_a_missed_resize()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();
        SimulateMissedResizePoll(tui, 120, 40);

        Redraw(shell, tui);

        tui.ScreenContains("Episode").Should().BeTrue();
        tui.ScreenContains("AudioPlayer").Should().BeTrue();
        tui.Lines().Should().OnlyContain(l => l.Length <= 120);
    }

    [Fact]
    public void ForceRedraw_also_recovers_from_a_shrink()
    {
        using var tui = new TuiHarness(cols: 120, rows: 40);
        var shell = BuildShell();
        tui.Render();
        SimulateMissedResizePoll(tui, 80, 25);

        Redraw(shell, tui);

        Application.Top.Frame.Width.Should().Be(80);
        tui.Lines().Should().OnlyContain(l => l.Length <= 80);
        tui.ScreenContains("AudioPlayer").Should().BeTrue();
    }

    [Fact]
    public void ForceRedraw_is_harmless_when_nothing_is_out_of_sync()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();
        var before = tui.Screen();

        Redraw(shell, tui);

        tui.Screen().Should().Be(before);
    }

    [Fact]
    public void ForceRedraw_can_be_called_repeatedly()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        var act = () => { for (int i = 0; i < 5; i++) Redraw(shell, tui); };

        act.Should().NotThrow();
        tui.ScreenContains("AudioPlayer").Should().BeTrue();
    }

    [Fact]
    public void NeedsRelayout_reports_the_desync()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        tui.Render();

        UiShell.NeedsRelayout().Should().BeFalse();

        SimulateMissedResizePoll(tui, 120, 40);
        UiShell.NeedsRelayout().Should().BeTrue();

        Redraw(shell, tui);
        UiShell.NeedsRelayout().Should().BeFalse();
    }
}
