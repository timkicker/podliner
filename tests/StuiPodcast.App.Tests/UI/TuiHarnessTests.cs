using FluentAssertions;
using Terminal.Gui;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// Guards the harness itself. If Terminal.Gui ever changes FakeDriver or
// FakeMainLoop these fail first, instead of every UI test failing for a
// reason that looks unrelated.
[Collection(TuiCollection.Name)]
public sealed class TuiHarnessTests
{
    [Fact]
    public void Initialises_headless_at_the_requested_size()
    {
        using var tui = new TuiHarness(cols: 100, rows: 30);

        tui.Cols.Should().Be(100);
        tui.Rows.Should().Be(30);
        Application.Driver.Should().BeSameAs(tui.Driver);
    }

    [Fact]
    public void Renders_a_view_and_reads_the_text_back()
    {
        using var tui = new TuiHarness();

        var win = new Window("podliner") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        win.Add(new Label("Episode list") { X = 2, Y = 2 });
        Application.Top.Add(win);

        tui.Render();

        tui.ScreenContains("Episode list").Should().BeTrue();
        tui.ScreenContains("podliner").Should().BeTrue();
    }

    [Fact]
    public void RowOf_finds_the_line_a_label_landed_on()
    {
        using var tui = new TuiHarness();

        var win = new Window { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        win.Add(new Label("marker") { X = 1, Y = 4 });
        Application.Top.Add(win);

        tui.Render();

        // +1 for the window border the label sits inside.
        tui.RowOf("marker").Should().Be(5);
    }

    [Fact]
    public void RowOf_returns_minus_one_when_absent()
    {
        using var tui = new TuiHarness();
        Application.Top.Add(new Window { Width = Dim.Fill(), Height = Dim.Fill() });

        tui.Render();

        tui.RowOf("nothing here").Should().Be(-1);
    }

    [Fact]
    public void Lines_returns_one_entry_per_row()
    {
        using var tui = new TuiHarness(cols: 40, rows: 12);
        Application.Top.Add(new Window { Width = Dim.Fill(), Height = Dim.Fill() });

        tui.Render();

        tui.Lines().Should().HaveCount(12);
    }

    [Fact]
    public void A_narrow_screen_still_renders()
    {
        using var tui = new TuiHarness(cols: 40, rows: 10);

        var win = new Window("p") { Width = Dim.Fill(), Height = Dim.Fill() };
        win.Add(new Label("narrow") { X = 1, Y = 1 });
        Application.Top.Add(win);

        tui.Render();

        tui.ScreenContains("narrow").Should().BeTrue();
        tui.Line(0).Length.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void Two_harnesses_in_sequence_do_not_leak_state()
    {
        using (var first = new TuiHarness())
        {
            var w = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
            w.Add(new Label("first run") { X = 1, Y = 1 });
            Application.Top.Add(w);
            first.Render();
            first.ScreenContains("first run").Should().BeTrue();
        }

        using var second = new TuiHarness();
        Application.Top.Add(new Window { Width = Dim.Fill(), Height = Dim.Fill() });
        second.Render();

        second.ScreenContains("first run").Should().BeFalse();
    }
}
