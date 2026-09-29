using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.UI;
using Xunit;

namespace Podliner.App.Tests.UI;

// #32: the reporter's library sat on stale episodes for months and nothing on
// screen said so. The feeds sidebar frame now carries the age of the feeds.
[Collection(TuiCollection.Name)]
public sealed class UiFeedsTitleTests
{
    private static UiShell Build()
    {
        var shell = new UiShell(new MemoryLogSink());
        shell.Build();
        return shell;
    }

    [Fact]
    public void The_sidebar_frame_shows_the_age_of_the_feeds()
    {
        using var tui = new TuiHarness(120, 30);
        var shell = Build();
        tui.Render();

        shell.SetFeedsTitle("Feeds · 12m ago");
        tui.Pump();
        tui.Render();

        tui.ScreenContains("Feeds · 12m ago").Should().BeTrue(tui.Screen());
    }

    [Fact]
    public void The_longest_title_fits_inside_the_frame()
    {
        using var tui = new TuiHarness(120, 30);
        var shell = Build();
        tui.Render();

        var now = DateTimeOffset.Now;
        foreach (var (age, failed) in new[] { (330, 999), (330, 5), (0, 100000), (400, 0) })
        {
            var title = Podliner.App.Services.FeedRefreshPolicy.SidebarTitle(now.AddDays(-age), now, failed, "·");
            shell.SetFeedsTitle(title);
            tui.Pump();
            tui.Render();

            tui.ScreenContains(title).Should().BeTrue($"'{title}' must show in full:\n{tui.Screen()}");
        }
    }
}
