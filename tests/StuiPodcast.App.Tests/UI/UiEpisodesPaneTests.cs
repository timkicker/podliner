using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.App.UI.Controls;
using StuiPodcast.Core;
using Terminal.Gui;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// The episode list, the biggest single control in the app and the one that
// switches behaviour per virtual feed: History orders by last-played and
// ignores search, Queue follows the queue order, everything else honours the
// sorter and the search term.
[Collection(TuiCollection.Name)]
public sealed class UiEpisodesPaneTests
{
    private static readonly Guid FeedA = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    private static readonly Guid FeedB = Guid.Parse("0b000000-0000-0000-0000-00000000000b");

    private static Episode Ep(string title, Guid? feed = null, int daysAgo = 1,
                              DateTimeOffset? lastPlayed = null, bool saved = false, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            FeedId = feed ?? FeedA,
            Title = title,
            AudioUrl = $"https://ex.test/{title}.mp3",
            PubDate = DateTimeOffset.UtcNow.AddDays(-daysAgo),
            Saved = saved,
            Progress = new EpisodeProgress { LastPlayedAt = lastPlayed },
        };

    private static (UiEpisodesPane pane, TuiHarness tui) Mount(int cols = 100)
    {
        var tui = new TuiHarness(cols, 25);
        var pane = new UiEpisodesPane();
        pane.Tabs.X = 0;
        pane.Tabs.Y = 0;
        pane.Tabs.Width = Dim.Fill();
        pane.Tabs.Height = Dim.Fill();
        Application.Top.Add(pane.Tabs);
        tui.Render();
        return (pane, tui);
    }

    // Feeds the pane through the same argument shape UiShell uses.
    private static void Set(UiEpisodesPane pane, IEnumerable<Episode> episodes, Guid feedId,
                            string? search = null,
                            Func<IEnumerable<Episode>, IEnumerable<Episode>>? sorter = null)
        => pane.SetEpisodes(episodes, feedId,
            VirtualFeedsCatalog.All, VirtualFeedsCatalog.Saved, VirtualFeedsCatalog.Downloaded,
            VirtualFeedsCatalog.History, VirtualFeedsCatalog.Queue,
            sorter, search, null);

    // ── rendering ───────────────────────────────────────────────────────────

    [Fact]
    public void Shows_the_three_tabs()
    {
        var (_, tui) = Mount();
        using var _t = tui;

        tui.ScreenContains("Episodes").Should().BeTrue();
        tui.ScreenContains("Details").Should().BeTrue();
        tui.ScreenContains("Chapters").Should().BeTrue();
    }

    [Fact]
    public void Lists_episode_titles()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, new[] { Ep("First"), Ep("Second") }, VirtualFeedsCatalog.All);
        tui.Render();

        tui.ScreenContains("First").Should().BeTrue();
        tui.ScreenContains("Second").Should().BeTrue();
    }

    [Fact]
    public void An_empty_list_renders_without_throwing()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        var act = () => { Set(pane, Array.Empty<Episode>(), VirtualFeedsCatalog.All); tui.Render(); };

        act.Should().NotThrow();
    }

    [Fact]
    public void The_chapters_tab_shows_its_count()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        pane.SetChaptersTabCount(7);
        tui.Render();

        tui.ScreenContains("Chapters (7)").Should().BeTrue();
    }

    [Fact]
    public void The_chapters_tab_drops_the_count_when_cleared()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        pane.SetChaptersTabCount(3);

        pane.SetChaptersTabCount(null);
        tui.Render();

        tui.ScreenContains("Chapters (").Should().BeFalse();
    }

    // ── selection ───────────────────────────────────────────────────────────

    [Fact]
    public void Selecting_an_index_reports_that_episode()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        var second = Ep("Second");
        Set(pane, new[] { Ep("First"), second }, VirtualFeedsCatalog.All);

        pane.SelectIndex(1);

        pane.GetSelected()!.Title.Should().Be("Second");
        pane.GetSelectedIndex().Should().Be(1);
    }

    [Fact]
    public void Selecting_past_the_end_clamps()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        Set(pane, new[] { Ep("Only") }, VirtualFeedsCatalog.All);

        pane.SelectIndex(99);

        pane.GetSelected()!.Title.Should().Be("Only");
    }

    [Fact]
    public void GetSelected_is_null_on_an_empty_list()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, Array.Empty<Episode>(), VirtualFeedsCatalog.All);

        pane.GetSelected().Should().BeNull();
    }

    // ── history view ────────────────────────────────────────────────────────

    [Fact]
    public void History_only_lists_episodes_that_were_played()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, new[]
        {
            Ep("Never Played"),
            Ep("Was Played", lastPlayed: DateTimeOffset.UtcNow.AddHours(-1)),
        }, VirtualFeedsCatalog.History);
        tui.Render();

        tui.ScreenContains("Was Played").Should().BeTrue();
        tui.ScreenContains("Never Played").Should().BeFalse();
    }

    [Fact]
    public void History_puts_the_most_recent_first()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, new[]
        {
            Ep("Older", lastPlayed: DateTimeOffset.UtcNow.AddDays(-3)),
            Ep("Newer", lastPlayed: DateTimeOffset.UtcNow.AddHours(-1)),
        }, VirtualFeedsCatalog.History);
        tui.Render();

        tui.RowOf("Newer").Should().BeLessThan(tui.RowOf("Older"));
    }

    [Fact]
    public void History_respects_its_limit()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        pane.SetHistoryLimit(10);

        var many = Enumerable.Range(0, 50)
            .Select(i => Ep($"Ep{i:00}", lastPlayed: DateTimeOffset.UtcNow.AddMinutes(-i)))
            .ToArray();
        Set(pane, many, VirtualFeedsCatalog.History);

        pane.SelectIndex(999);
        pane.GetSelectedIndex().Should().BeLessThan(10);
    }

    // ── queue view ──────────────────────────────────────────────────────────

    [Fact]
    public void Queue_follows_the_queue_order_not_the_publish_date()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        var a = Ep("Alpha", daysAgo: 1);
        var b = Ep("Bravo", daysAgo: 9);
        pane.SetQueueOrder(new[] { b.Id, a.Id });

        Set(pane, new[] { a, b }, VirtualFeedsCatalog.Queue);
        tui.Render();

        tui.RowOf("Bravo").Should().BeLessThan(tui.RowOf("Alpha"));
    }

    [Fact]
    public void Queue_skips_ids_that_are_no_longer_episodes()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        var a = Ep("Alpha");
        pane.SetQueueOrder(new[] { Guid.NewGuid(), a.Id });

        Set(pane, new[] { a }, VirtualFeedsCatalog.Queue);
        tui.Render();

        tui.ScreenContains("Alpha").Should().BeTrue();
        pane.GetSelected().Should().NotBeNull();
    }

    [Fact]
    public void An_empty_queue_renders_nothing()
    {
        var (pane, tui) = Mount();
        using var _t = tui;
        pane.SetQueueOrder(Array.Empty<Guid>());

        Set(pane, new[] { Ep("Alpha") }, VirtualFeedsCatalog.Queue);

        pane.GetSelected().Should().BeNull();
    }

    // ── search ──────────────────────────────────────────────────────────────

    [Fact]
    public void Search_filters_the_list()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, new[] { Ep("Rust Talk"), Ep("Go Talk") }, VirtualFeedsCatalog.All, search: "rust");
        tui.Render();

        tui.ScreenContains("Rust Talk").Should().BeTrue();
        tui.ScreenContains("Go Talk").Should().BeFalse();
    }

    [Fact]
    public void Search_ignores_case()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, new[] { Ep("Rust Talk") }, VirtualFeedsCatalog.All, search: "RUST");
        tui.Render();

        tui.ScreenContains("Rust Talk").Should().BeTrue();
    }

    [Fact]
    public void History_ignores_the_search_term()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, new[] { Ep("Played", lastPlayed: DateTimeOffset.UtcNow) },
            VirtualFeedsCatalog.History, search: "nomatch");
        tui.Render();

        tui.ScreenContains("Played").Should().BeTrue();
    }

    // ── sorter ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_sorter_decides_the_order_outside_history_and_queue()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        Set(pane, new[] { Ep("Bravo"), Ep("Alpha") }, VirtualFeedsCatalog.All,
            sorter: eps => eps.OrderBy(e => e.Title));
        tui.Render();

        tui.RowOf("Alpha").Should().BeLessThan(tui.RowOf("Bravo"));
    }

    [Fact]
    public void Queue_ignores_the_sorter()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        var a = Ep("Alpha");
        var b = Ep("Bravo");
        pane.SetQueueOrder(new[] { b.Id, a.Id });

        Set(pane, new[] { a, b }, VirtualFeedsCatalog.Queue,
            sorter: eps => eps.OrderBy(e => e.Title));
        tui.Render();

        tui.RowOf("Bravo").Should().BeLessThan(tui.RowOf("Alpha"));
    }

    // ── now playing ─────────────────────────────────────────────────────────

    [Fact]
    public void The_now_playing_row_gets_a_marker()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        var (pane, tui) = Mount();
        using var _t = tui;

        var playing = Ep("Playing Now");
        Set(pane, new[] { playing, Ep("Other") }, VirtualFeedsCatalog.All);
        pane.InjectNowPlaying(playing.Id);
        tui.Render();

        tui.Line(tui.RowOf("Playing Now")).Should().Contain("▶");
    }

    [Fact]
    public void Clearing_now_playing_removes_the_marker()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        var (pane, tui) = Mount();
        using var _t = tui;

        var playing = Ep("Playing Now");
        Set(pane, new[] { playing }, VirtualFeedsCatalog.All);
        pane.InjectNowPlaying(playing.Id);
        tui.Render();

        pane.InjectNowPlaying(null);
        tui.Render();

        tui.Line(tui.RowOf("Playing Now")).Should().NotContain("▶");
    }

    // ── details tab ─────────────────────────────────────────────────────────

    [Fact]
    public void ShowDetails_fills_the_details_tab()
    {
        // ShowDetails only fills the text; switching to the tab is the
        // shell's job (the `i` key), so the test activates it here.
        var (pane, tui) = Mount();
        using var _t = tui;
        var ep = Ep("With Notes");
        ep.DescriptionText = "These are the shownotes.";

        pane.ShowDetails(ep);
        pane.Tabs.SelectedTab = pane.DetailsTab;
        tui.Render();

        tui.ScreenContains("shownotes").Should().BeTrue();
        tui.ScreenContains("With Notes").Should().BeTrue();
    }

    [Fact]
    public void ShowDetails_says_so_when_there_are_no_shownotes()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        pane.ShowDetails(Ep("Bare"));
        pane.Tabs.SelectedTab = pane.DetailsTab;
        tui.Render();

        tui.ScreenContains("(no shownotes)").Should().BeTrue();
    }

    [Fact]
    public void ShowDetails_handles_an_episode_without_notes()
    {
        var (pane, tui) = Mount();
        using var _t = tui;

        var act = () => { pane.ShowDetails(Ep("No Notes")); tui.Render(); };

        act.Should().NotThrow();
    }

    // ── narrow terminals ────────────────────────────────────────────────────

    [Fact]
    public void Rows_stay_inside_a_narrow_pane()
    {
        var (pane, tui) = Mount(cols: 50);
        using var _t = tui;

        Set(pane, new[] { Ep("A Really Quite Long Episode Title Here") }, VirtualFeedsCatalog.All);
        tui.Render();

        tui.Lines().Should().OnlyContain(l => l.Length <= 50);
    }
}
