using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// Renders the real UiShell headless and asserts on the painted screen.
// These are the first tests that cover the ~2900 lines of UI code, and the
// glyph and resize cases are regressions: the play button broke twice
// (issue #1 and the v1.1.0 entry) and layout after a resize is issue #4.
[Collection(TuiCollection.Name)]
public sealed class UiShellRenderTests
{
    private static readonly Guid FeedId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static Feed MakeFeed(string title) => new() { Id = FeedId, Title = title, Url = "https://ex.test/f.xml" };

    private static Episode MakeEpisode(string title, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        FeedId = FeedId,
        Title = title,
        AudioUrl = "https://ex.test/a.mp3",
        PubDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static UiShell BuildShell()
    {
        var shell = new UiShell(new MemoryLogSink());
        shell.Build();
        return shell;
    }

    // ── layout ──────────────────────────────────────────────────────────────

    [Fact]
    public void Builds_and_renders_without_a_terminal()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        var act = () => tui.Render();

        act.Should().NotThrow();
        tui.Screen().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Shows_the_player_panel()
    {
        using var tui = new TuiHarness();
        BuildShell();

        tui.Render();

        tui.ScreenContains("AudioPlayer").Should().BeTrue();
    }

    [Fact]
    public void Shows_feed_titles_in_the_sidebar()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetFeeds(new[] { MakeFeed("Darknet Diaries") });
        tui.Render();

        tui.ScreenContains("Darknet Diaries").Should().BeTrue();
    }

    [Fact]
    public void Shows_episode_titles_in_the_list()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetFeeds(new[] { MakeFeed("Feed") });
        shell.SetEpisodesForFeed(FeedId, new[] { MakeEpisode("Test Episode") });
        tui.Render();

        tui.ScreenContains("Test Episode").Should().BeTrue();
    }

    [Fact]
    public void Virtual_feeds_are_listed_above_real_ones()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetFeeds(new[] { MakeFeed("Real Feed") });
        tui.Render();

        var allRow = tui.RowOf("All");
        var realRow = tui.RowOf("Real Feed");

        allRow.Should().BeGreaterThanOrEqualTo(0);
        realRow.Should().BeGreaterThan(allRow);
    }

    // ── now-playing glyph ───────────────────────────────────────────────────

    [Fact]
    public void Marks_the_now_playing_episode_with_the_unicode_glyph()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        using var tui = new TuiHarness();
        var shell = BuildShell();

        var ep = MakeEpisode("Now Playing");
        shell.SetFeeds(new[] { MakeFeed("Feed") });
        shell.SetEpisodesForFeed(FeedId, new[] { ep });
        shell.SetNowPlaying(ep.Id);
        tui.Render();

        var row = tui.RowOf("Now Playing");
        row.Should().BeGreaterThanOrEqualTo(0);
        tui.Line(row).Should().Contain("▶");
    }

    [Fact]
    public void Falls_back_to_an_ascii_marker_when_unicode_is_off()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Ascii);
        try
        {
            using var tui = new TuiHarness();
            var shell = BuildShell();

            var ep = MakeEpisode("Ascii Episode");
            shell.SetFeeds(new[] { MakeFeed("Feed") });
            shell.SetEpisodesForFeed(FeedId, new[] { ep });
            shell.SetNowPlaying(ep.Id);
            tui.Render();

            var row = tui.RowOf("Ascii Episode");
            row.Should().BeGreaterThanOrEqualTo(0);
            tui.Line(row).Should().Contain(">");
            tui.Screen().Should().NotContain("▶");
        }
        finally
        {
            UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        }
    }

    [Fact]
    public void An_episode_that_is_not_playing_has_no_marker()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetFeeds(new[] { MakeFeed("Feed") });
        shell.SetEpisodesForFeed(FeedId, new[] { MakeEpisode("Idle Episode") });
        tui.Render();

        var row = tui.RowOf("Idle Episode");
        row.Should().BeGreaterThanOrEqualTo(0);
        tui.Line(row).Should().NotContain("▶");
    }

    [Fact]
    public void A_title_wider_than_the_column_is_truncated_with_an_ellipsis()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetFeeds(new[] { MakeFeed("Feed") });
        shell.SetEpisodesForFeed(FeedId, new[] { MakeEpisode("An Extremely Long Episode Title That Cannot Fit") });
        tui.Render();

        var row = tui.RowOf("An Extremely");
        row.Should().BeGreaterThanOrEqualTo(0);

        var line = tui.Line(row);
        line.Should().Contain("\u2026");
        line.Length.Should().BeLessThanOrEqualTo(tui.Cols);
    }

    // ── resize (issue #4) ───────────────────────────────────────────────────

    [Fact]
    public void Survives_a_resize_and_keeps_rendering_content()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetFeeds(new[] { MakeFeed("Feed") });
        shell.SetEpisodesForFeed(FeedId, new[] { MakeEpisode("Resize Episode") });
        tui.Render();
        tui.ScreenContains("Resize Episode").Should().BeTrue();

        tui.Resize(120, 40);

        tui.Cols.Should().Be(120);
        tui.Rows.Should().Be(40);
        tui.ScreenContains("Resize Episode").Should().BeTrue();
        tui.ScreenContains("AudioPlayer").Should().BeTrue();
    }

    [Fact]
    public void Shrinking_does_not_push_content_off_screen()
    {
        using var tui = new TuiHarness(cols: 120, rows: 40);
        var shell = BuildShell();

        shell.SetFeeds(new[] { MakeFeed("Feed") });
        shell.SetEpisodesForFeed(FeedId, new[] { MakeEpisode("Small Screen") });
        tui.Render();

        tui.Resize(80, 25);

        tui.Cols.Should().Be(80);
        tui.ScreenContains("AudioPlayer").Should().BeTrue();
        // No row may run past the new width.
        tui.Lines().Should().OnlyContain(l => l.Length <= 80);
    }

    // ── window title ────────────────────────────────────────────────────────

    [Fact]
    public void Window_title_shows_the_current_episode()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetWindowTitle("Episode 42");
        tui.Render();

        tui.ScreenContains("Episode 42").Should().BeTrue();
    }

    [Fact]
    public void Window_title_falls_back_to_a_dash_when_empty()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetWindowTitle(null);
        tui.Render();

        tui.ScreenContains("—").Should().BeTrue();
    }
}
