using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.UI;
using Serilog.Events;
using Serilog.Parsing;
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
}
