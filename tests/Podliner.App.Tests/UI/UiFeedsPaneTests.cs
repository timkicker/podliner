using FluentAssertions;
using Podliner.App.Services;
using Podliner.App.UI.Controls;
using Podliner.Core;
using Terminal.Gui;
using Xunit;

namespace Podliner.App.Tests.UI;

// The feeds sidebar. Virtual feeds (All / Saved / Downloaded / Queue /
// History) share the list with real subscriptions and are separated by a
// non-selectable barrier row, so selection has to skip over it.
[Collection(TuiCollection.Name)]
public sealed class UiFeedsPaneTests
{
    private static Feed Real(string title, Guid? id = null)
        => new() { Id = id ?? Guid.NewGuid(), Title = title, Url = "https://ex.test/f.xml" };

    private static Feed Virt(Guid id, string title) => new() { Id = id, Title = title };

    private static IReadOnlyList<Feed> WithVirtuals(params Feed[] real)
    {
        var list = new List<Feed>
        {
            Virt(VirtualFeedsCatalog.All, "All Episodes"),
            Virt(VirtualFeedsCatalog.Saved, "★ Saved"),
            Virt(VirtualFeedsCatalog.Downloaded, "⬇ Downloaded"),
            Virt(VirtualFeedsCatalog.Queue, "⧉ Queue"),
            Virt(VirtualFeedsCatalog.History, "⏱ History"),
            Virt(VirtualFeedsCatalog.Seperator, "────────"),
        };
        list.AddRange(real);
        return list;
    }

    private static (UiFeedsPane pane, TuiHarness tui) Mount(int cols = 80)
    {
        var tui = new TuiHarness(cols, 25);
        var pane = new UiFeedsPane();
        pane.Frame.X = 0;
        pane.Frame.Y = 0;
        pane.Frame.Width = Dim.Fill();
        pane.Frame.Height = Dim.Fill();
        Application.Top.Add(pane.Frame);
        tui.Render();
        return (pane, tui);
    }

    // ── rendering ───────────────────────────────────────────────────────────

    [Fact]
    public void Renders_its_frame()
    {
        var (_, tui) = Mount();
        using var _t = tui;

        tui.ScreenContains("Feeds").Should().BeTrue();
    }

    [Fact]
    public void Shows_real_feed_titles()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        pane.SetFeeds(WithVirtuals(Real("Darknet Diaries"), Real("Lage der Nation")));
        tui.Render();

        tui.ScreenContains("Darknet Diaries").Should().BeTrue();
        tui.ScreenContains("Lage der Nation").Should().BeTrue();
    }

    [Fact]
    public void Shows_the_virtual_feeds()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        pane.SetFeeds(WithVirtuals(Real("Feed")));
        tui.Render();

        tui.ScreenContains("All Episodes").Should().BeTrue();
        tui.ScreenContains("Saved").Should().BeTrue();
        tui.ScreenContains("Downloaded").Should().BeTrue();
        tui.ScreenContains("Queue").Should().BeTrue();
        tui.ScreenContains("History").Should().BeTrue();
    }

    [Fact]
    public void Virtual_feeds_come_before_real_ones()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        pane.SetFeeds(WithVirtuals(Real("Real Feed")));
        tui.Render();

        tui.RowOf("All Episodes").Should().BeLessThan(tui.RowOf("Real Feed"));
    }

    [Fact]
    public void Renders_an_empty_list_without_throwing()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        var act = () => { pane.SetFeeds(Array.Empty<Feed>()); tui.Render(); };

        act.Should().NotThrow();
    }

    // ── selection ───────────────────────────────────────────────────────────

    [Fact]
    public void Selecting_a_real_feed_reports_its_id()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        var feed = Real("Target");
        pane.SetFeeds(WithVirtuals(feed));

        pane.SelectFeed(feed.Id);

        pane.GetSelectedFeedId().Should().Be(feed.Id);
    }

    [Fact]
    public void Selecting_a_virtual_feed_reports_its_id()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        pane.SetFeeds(WithVirtuals(Real("Feed")));

        pane.SelectFeed(VirtualFeedsCatalog.Saved);

        pane.GetSelectedFeedId().Should().Be(VirtualFeedsCatalog.Saved);
    }

    [Fact]
    public void Selecting_an_unknown_id_leaves_the_selection_alone()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        var feed = Real("Target");
        pane.SetFeeds(WithVirtuals(feed));
        pane.SelectFeed(feed.Id);

        pane.SelectFeed(Guid.NewGuid());

        pane.GetSelectedFeedId().Should().Be(feed.Id);
    }

    [Fact]
    public void The_barrier_row_is_never_reported_as_a_selection()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        pane.SetFeeds(WithVirtuals(Real("Feed")));

        pane.SelectFeed(VirtualFeedsCatalog.Seperator);

        pane.GetSelectedFeedId().Should().NotBe(VirtualFeedsCatalog.Seperator);
    }

    [Fact]
    public void SelectedChanged_fires_when_the_row_moves()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        var feed = Real("Target");
        pane.SetFeeds(WithVirtuals(feed));

        var fired = 0;
        pane.SelectedChanged += () => fired++;
        pane.SelectFeed(feed.Id);

        fired.Should().BeGreaterThan(0);
    }

    // ── state ───────────────────────────────────────────────────────────────

    [Fact]
    public void RawFeeds_mirrors_what_was_set()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        var feeds = WithVirtuals(Real("A"), Real("B"));

        pane.SetFeeds(feeds);

        pane.RawFeeds.Should().HaveCount(feeds.Count);
    }

    [Fact]
    public void Setting_feeds_twice_replaces_rather_than_appends()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        pane.SetFeeds(WithVirtuals(Real("First")));
        pane.SetFeeds(WithVirtuals(Real("Second")));
        tui.Render();

        tui.ScreenContains("Second").Should().BeTrue();
        tui.ScreenContains("First").Should().BeFalse();
        pane.RawFeeds.Should().HaveCount(7);
    }

    // ── narrow terminals ────────────────────────────────────────────────────

    [Fact]
    public void A_long_title_stays_inside_the_pane()
    {
        var (pane, tui) = Mount(cols: 40);
        using var _t = tui;

        pane.SetFeeds(WithVirtuals(Real("An Extremely Long Podcast Title That Will Not Fit")));
        tui.Render();

        tui.Lines().Should().OnlyContain(l => l.Length <= 40);
    }
}
