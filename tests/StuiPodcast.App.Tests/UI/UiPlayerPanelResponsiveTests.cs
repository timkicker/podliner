using FluentAssertions;
using StuiPodcast.App.UI.Controls;
using Terminal.Gui;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// The control row is a left transport block, a speed block and a right-hand
// volume block. Before this, all three were laid out independently, so below
// roughly 140 columns they ran into each other: at the default 80x25 the skip
// and download buttons disappeared and the volume block painted straight over
// the speed buttons.
//
// Controls are now dropped in order of redundancy as the terminal narrows
// (download duplicates `d`, the skips duplicate the arrow keys, the volume
// bar is decoration next to the percentage, the ±spd buttons duplicate `[`
// and `]`). These tests pin the tiers and, more importantly, assert that
// nothing ever collides at any width.
[Collection(TuiCollection.Name)]
public sealed class UiPlayerPanelResponsiveTests
{
    private static string Fmt(TimeSpan t) => $"{t.Minutes:00}:{t.Seconds:00}";

    private static (UiPlayerPanel panel, TuiHarness tui) Mount(int cols)
    {
        var tui = new TuiHarness(cols, 12);
        var panel = new UiPlayerPanel { X = 0, Y = 0, Width = Dim.Fill(), Height = UiPlayerPanel.PlayerFrameH };
        Application.Top.Add(panel);
        tui.Render();
        panel.Update(
            PlaybackSnapshot.From(1, Guid.NewGuid(), TimeSpan.FromSeconds(65), TimeSpan.FromMinutes(10),
                true, 1.5, DateTimeOffset.Now),
            volume0to100: 42, Fmt);
        tui.Render();
        return (panel, tui);
    }

    // The row every control lives on.
    private static string ControlRow(TuiHarness tui) => tui.Line(tui.RowOf("Vol+"));

    // ── nothing ever collides ───────────────────────────────────────────────

    [Theory]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(78)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(104)]
    [InlineData(111)]
    [InlineData(112)]
    [InlineData(140)]
    [InlineData(160)]
    [InlineData(200)]
    public void Controls_never_overlap_at_any_width(int cols)
    {
        var (panel, tui) = Mount(cols);
        using var _t = tui;

        var row = ControlRow(tui);

        // A control painting over its neighbour truncates the one underneath,
        // which always shows up as an opening bracket with no closing one.
        row.Count(c => c == '[').Should().Be(row.Count(c => c == ']'),
            $"every button must render whole at {cols} columns, got: {row}");
        row.Length.Should().BeLessThanOrEqualTo(cols);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(104)]
    [InlineData(140)]
    [InlineData(200)]
    public void The_play_button_and_volume_are_always_reachable(int cols)
    {
        var (panel, tui) = Mount(cols);
        using var _t = tui;

        var row = ControlRow(tui);

        row.Should().Contain("Pause");
        row.Should().Contain("Vol−");
        row.Should().Contain("Vol+");
        row.Should().Contain("42%");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(104)]
    [InlineData(200)]
    public void The_speed_readout_is_always_visible(int cols)
    {
        var (panel, tui) = Mount(cols);
        using var _t = tui;

        ControlRow(tui).Should().Contain("1.5");
    }

    // ── tiers ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_wide_terminal_shows_everything()
    {
        var (panel, tui) = Mount(160);
        using var _t = tui;

        var row = ControlRow(tui);
        row.Should().Contain("10s»");
        row.Should().Contain("Download");
        row.Should().Contain("-spd");
        row.Should().Contain("+spd");
    }

    [Fact]
    public void The_download_button_is_the_first_to_go()
    {
        var (panel, tui) = Mount(120);
        using var _t = tui;

        var row = ControlRow(tui);
        row.Should().NotContain("Download");
        row.Should().Contain("10s»");   // skips survive one tier longer
    }

    [Fact]
    public void The_skip_buttons_go_at_the_default_terminal_size()
    {
        var (panel, tui) = Mount(80);
        using var _t = tui;

        var row = ControlRow(tui);
        row.Should().NotContain("10s»");
        row.Should().NotContain("«10s");
        row.Should().Contain("-spd");   // speed buttons still fit
    }

    [Fact]
    public void The_speed_buttons_go_last()
    {
        var (panel, tui) = Mount(60);
        using var _t = tui;

        var row = ControlRow(tui);
        row.Should().NotContain("-spd");
        row.Should().NotContain("+spd");
        row.Should().Contain("1.5");    // the readout stays
    }

    [Fact]
    public void The_volume_bar_only_appears_when_there_is_room()
    {
        var (narrow, tuiN) = Mount(104);
        using (tuiN) ControlRow(tuiN).Should().NotContain("█");

        var (wide, tuiW) = Mount(112);
        using (tuiW) ControlRow(tuiW).Should().Contain("█");
    }

    // ── resizing between tiers ──────────────────────────────────────────────

    [Fact]
    public void Growing_the_terminal_brings_controls_back()
    {
        var (panel, tui) = Mount(80);
        using var _t = tui;
        ControlRow(tui).Should().NotContain("Download");

        tui.Resize(160, 20);

        ControlRow(tui).Should().Contain("Download");
    }

    [Fact]
    public void Shrinking_the_terminal_drops_controls_without_collision()
    {
        var (panel, tui) = Mount(160);
        using var _t = tui;
        ControlRow(tui).Should().Contain("Download");

        tui.Resize(80, 20);

        var row = ControlRow(tui);
        row.Should().NotContain("Download");
        row.Count(c => c == '[').Should().Be(row.Count(c => c == ']'));
    }
}
