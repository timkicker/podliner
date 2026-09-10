using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// Themes have needed rework twice (the v1.0.0 "Rework default theme" entry and
// the macOS play-button issues). A theme that throws or paints the UI into
// unreadable colours is only visible by looking, so these render each one and
// check the screen still contains the things the user needs to see.
[Collection(TuiCollection.Name)]
public sealed class UiThemeRenderTests
{
    private static readonly Guid FeedId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static UiShell BuildShell()
    {
        var shell = new UiShell(new MemoryLogSink());
        shell.Build();
        shell.SetFeeds(new[] { new Feed { Id = FeedId, Title = "Test Feed", Url = "https://ex.test/f.xml" } });
        shell.SetEpisodesForFeed(FeedId, new[]
        {
            new Episode
            {
                Id = Guid.NewGuid(), FeedId = FeedId, Title = "Test Episode",
                AudioUrl = "https://ex.test/a.mp3",
                PubDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            }
        });
        return shell;
    }

    [Theory]
    [InlineData(ThemeMode.Base)]
    [InlineData(ThemeMode.MenuAccent)]
    [InlineData(ThemeMode.Native)]
    [InlineData(ThemeMode.User)]
    public void Every_theme_applies_without_throwing(ThemeMode mode)
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        var act = () => { shell.SetTheme(mode); tui.Render(); };

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(ThemeMode.Base)]
    [InlineData(ThemeMode.MenuAccent)]
    [InlineData(ThemeMode.Native)]
    [InlineData(ThemeMode.User)]
    public void Every_theme_still_shows_the_content(ThemeMode mode)
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetTheme(mode);
        tui.Render();

        tui.ScreenContains("Test Feed").Should().BeTrue();
        tui.ScreenContains("Test Episode").Should().BeTrue();
        tui.ScreenContains("AudioPlayer").Should().BeTrue();
    }

    [Theory]
    [InlineData(ThemeMode.Base)]
    [InlineData(ThemeMode.MenuAccent)]
    [InlineData(ThemeMode.Native)]
    [InlineData(ThemeMode.User)]
    public void Every_theme_reports_itself_back(ThemeMode mode)
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetTheme(mode);

        shell.CurrentTheme.Should().Be(mode);
    }

    [Fact]
    public void The_toggle_visits_every_theme_and_comes_back()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        shell.SetTheme(ThemeMode.Base);

        var seen = new List<ThemeMode> { shell.CurrentTheme };
        for (int i = 0; i < 4; i++)
        {
            shell.ToggleTheme();
            seen.Add(shell.CurrentTheme);
        }

        // Four steps from Base must land back on Base, having passed through
        // each of the other three exactly once.
        seen.Last().Should().Be(ThemeMode.Base);
        seen.Take(4).Should().BeEquivalentTo(Enum.GetValues<ThemeMode>());
    }

    [Fact]
    public void Toggling_fires_the_change_event()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        var fired = new List<ThemeMode>();
        shell.ThemeChanged += m => fired.Add(m);
        shell.ToggleTheme();

        fired.Should().ContainSingle().Which.Should().Be(shell.CurrentTheme);
    }

    [Fact]
    public void Switching_themes_repeatedly_stays_stable()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        foreach (var mode in Enum.GetValues<ThemeMode>())
        {
            shell.SetTheme(mode);
            tui.Render();
        }
        foreach (var mode in Enum.GetValues<ThemeMode>().Reverse())
        {
            shell.SetTheme(mode);
            tui.Render();
        }

        tui.ScreenContains("Test Episode").Should().BeTrue();
    }

    [Fact]
    public void A_theme_survives_a_resize()
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();
        shell.SetTheme(ThemeMode.Native);
        tui.Render();

        tui.Resize(120, 40);

        tui.ScreenContains("Test Episode").Should().BeTrue();
        shell.CurrentTheme.Should().Be(ThemeMode.Native);
    }

    [Theory]
    [InlineData(1, ThemeMode.Base)]
    [InlineData(2, ThemeMode.MenuAccent)]
    [InlineData(3, ThemeMode.Native)]
    public void The_numeric_shortcuts_pick_a_theme(int n, ThemeMode expected)
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetThemeByNumber(n);

        shell.CurrentTheme.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void An_unknown_theme_number_falls_back_to_base(int n)
    {
        using var tui = new TuiHarness();
        var shell = BuildShell();

        shell.SetThemeByNumber(n);

        shell.CurrentTheme.Should().Be(ThemeMode.Base);
    }
}
