using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.UI;
using Serilog.Events;
using Serilog.Parsing;
using Terminal.Gui;
using Xunit;

namespace Podliner.App.Tests.UI;

// The logs overlay is what the bug-report template tells people to paste, so
// it has to show the log rather than a single line above 27 blank rows.
[Collection(TuiCollection.Name)]
public sealed class UiShellLogsDialogTests
{
    private static MemoryLogSink SinkWith(int count)
    {
        var sink = new MemoryLogSink();
        var parser = new MessageTemplateParser();
        for (int i = 0; i < count; i++)
            sink.Emit(new LogEvent(
                DateTimeOffset.UnixEpoch,
                LogEventLevel.Information,
                exception: null,
                parser.Parse($"line-{i}"),
                Array.Empty<LogEventProperty>()));
        return sink;
    }

    [Fact]
    public void A_short_log_is_shown_from_its_first_line()
    {
        using var h = new TuiHarness(120, 40);
        var sink = SinkWith(5);

        var dlg = UiShellLogsDialog.Build(sink);
        h.Render(dlg);

        h.ScreenContains("line-0").Should().BeTrue(
            "the whole log fits, so nothing should be scrolled off the top:\n" + h.Screen());
        h.ScreenContains("line-4").Should().BeTrue(h.Screen());
    }

    [Fact]
    public void A_long_log_is_scrolled_to_its_newest_lines()
    {
        using var h = new TuiHarness(120, 40);
        var sink = SinkWith(200);

        var dlg = UiShellLogsDialog.Build(sink);
        h.Render(dlg);

        h.ScreenContains("line-199").Should().BeTrue(
            "the newest line is the one people came for:\n" + h.Screen());
        h.ScreenContains("line-0").Should().BeFalse("200 lines do not fit a 28-row view");
    }
    static TextView LogView(View v)
    {
        if (v is TextView tv) return tv;
        foreach (var c in v.Subviews)
            if (FindIn(c) is { } hit) return hit;
        throw new InvalidOperationException("no text view in the dialog");

        static TextView? FindIn(View v)
        {
            if (v is TextView tv) return tv;
            foreach (var c in v.Subviews)
                if (FindIn(c) is { } hit) return hit;
            return null;
        }
    }

    static void Wheel(TextView tv, MouseFlags flags, int times)
    {
        for (int i = 0; i < times; i++)
            tv.MouseEvent(new MouseEvent { Flags = flags, X = 1, Y = 1, View = tv });
    }

    // #42: the wheel scrolled past the last line until one line sat above
    // a blank view.
    [Fact]
    public void Wheel_down_stops_at_the_last_line()
    {
        using var h = new TuiHarness(120, 40);
        var sink = SinkWith(200);
        var dlg = UiShellLogsDialog.Build(sink);
        h.Render(dlg);
        var tv = LogView(dlg);
        var full = tv.TopRow;

        Wheel(tv, MouseFlags.WheeledDown, 40);
        h.Render(dlg);

        tv.TopRow.Should().Be(full, "the view was already at the bottom:\n" + h.Screen());
        h.ScreenContains("line-199").Should().BeTrue(h.Screen());
        h.ScreenContains($"line-{200 - tv.Frame.Height}").Should().BeTrue("the view stays full:\n" + h.Screen());
    }

    [Fact]
    public void Wheel_up_then_down_comes_back_to_a_full_view()
    {
        using var h = new TuiHarness(120, 40);
        var dlg = UiShellLogsDialog.Build(SinkWith(200));
        h.Render(dlg);
        var tv = LogView(dlg);
        var bottom = tv.TopRow;

        Wheel(tv, MouseFlags.WheeledUp, 10);
        tv.TopRow.Should().Be(bottom - 10);
        Wheel(tv, MouseFlags.WheeledDown, 30);
        h.Render(dlg);

        tv.TopRow.Should().Be(bottom, h.Screen());
    }

    [Fact]
    public void Wheel_down_on_a_short_log_does_not_move_it()
    {
        using var h = new TuiHarness(120, 40);
        var dlg = UiShellLogsDialog.Build(SinkWith(5));
        h.Render(dlg);
        var tv = LogView(dlg);

        Wheel(tv, MouseFlags.WheeledDown, 10);
        h.Render(dlg);

        tv.TopRow.Should().Be(0);
        h.ScreenContains("line-0").Should().BeTrue(h.Screen());
    }
}
