using FluentAssertions;
using Podliner.App.UI;
using Podliner.App.UI.Controls;
using Terminal.Gui;
using Xunit;

namespace Podliner.App.Tests.UI;

// The player panel is where the play button, clock and volume live. Both
// play-button regressions in the changelog (issue #1 and the v1.1.0 entry)
// landed here, and until now none of it was covered.
[Collection(TuiCollection.Name)]
public sealed class UiPlayerPanelTests
{
    private static string Fmt(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes:00}:{t.Seconds:00}";

    private static UiPlayerPanel Mount(TuiHarness tui)
    {
        var panel = new UiPlayerPanel { X = 0, Y = 0, Width = Dim.Fill(), Height = UiPlayerPanel.PlayerFrameH };
        Application.Top.Add(panel);
        tui.Render();
        return panel;
    }

    private static PlaybackSnapshot Snap(long posMs, long lenMs, bool playing, double speed = 1.0)
        => PlaybackSnapshot.From(1, Guid.NewGuid(),
            TimeSpan.FromMilliseconds(posMs), TimeSpan.FromMilliseconds(lenMs),
            playing, speed, DateTimeOffset.Now);

    // ── framing ─────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_its_frame_title()
    {
        using var tui = new TuiHarness();
        Mount(tui);

        tui.ScreenContains("AudioPlayer").Should().BeTrue();
    }

    [Fact]
    public void Reserves_a_fixed_height()
        => UiPlayerPanel.PlayerFrameH.Should().Be(7);

    // ── clock ───────────────────────────────────────────────────────────────

    [Fact]
    public void Shows_position_and_length()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.Update(Snap(65_000, 600_000, playing: true), volume0to100: 50, Fmt);
        tui.Render();

        tui.ScreenContains("01:05").Should().BeTrue();
        tui.ScreenContains("10:00").Should().BeTrue();
    }

    [Fact]
    public void Shows_an_unknown_length_as_dashes()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.Update(Snap(0, 0, playing: false), volume0to100: 50, Fmt);
        tui.Render();

        tui.ScreenContains("--:--").Should().BeTrue();
    }

    [Fact]
    public void Formats_an_hour_long_episode()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.Update(Snap(3_723_000, 7_200_000, playing: true), volume0to100: 50, Fmt);
        tui.Render();

        tui.ScreenContains("1:02:03").Should().BeTrue();
    }

    // ── volume ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(100)]
    public void Shows_the_volume_percentage(int volume)
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.Update(Snap(0, 600_000, playing: false), volume, Fmt);
        tui.Render();

        tui.ScreenContains($"{volume}%").Should().BeTrue();
    }

    // ── speed ───────────────────────────────────────────────────────────────

    [Fact]
    public void Shows_the_playback_speed()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.Update(Snap(0, 600_000, playing: true, speed: 1.5), 50, Fmt);
        tui.Render();

        tui.ScreenContains("1.5").Should().BeTrue();
    }

    [Fact]
    public void Speed_buttons_can_be_disabled_for_engines_without_speed_support()
    {
        // MediaFoundation has no rate control; the buttons must not look
        // usable there (issue #18).
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.SetSpeedEnabled(false);
        tui.Render();

        panel.BtnSpeedUp.Enabled.Should().BeFalse();
        panel.BtnSpeedDown.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Speed_buttons_are_enabled_again_for_a_capable_engine()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.SetSpeedEnabled(false);
        panel.SetSpeedEnabled(true);

        panel.BtnSpeedUp.Enabled.Should().BeTrue();
        panel.BtnSpeedDown.Enabled.Should().BeTrue();
    }

    // ── loading overlay ─────────────────────────────────────────────────────

    [Fact]
    public void Loading_disables_the_play_button_and_shows_the_message()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.SetLoading(true, "loading…");
        tui.Render();

        panel.BtnPlayPause.Enabled.Should().BeFalse();
        tui.ScreenContains("loading").Should().BeTrue();
    }

    [Fact]
    public void Clearing_loading_re_enables_the_play_button()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);
        panel.SetLoading(true, "loading…");

        panel.SetLoading(false);

        panel.BtnPlayPause.Enabled.Should().BeTrue();
    }

    [Fact]
    public void The_slow_network_message_reaches_the_screen()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.SetLoading(true, "slow…");
        tui.Render();

        tui.ScreenContains("slow").Should().BeTrue();
    }

    [Fact]
    public void A_long_loading_message_cannot_widen_the_play_button()
    {
        // Regression: Button.AutoSize let the button grow with its text and
        // overlap the speed and volume controls, garbling the whole row.
        using var tui = new TuiHarness();
        var panel = Mount(tui);
        var normalWidth = panel.BtnPlayPause.Frame.Width;

        panel.SetLoading(true, "still connecting… check your network");
        tui.Render();

        panel.BtnPlayPause.Frame.Width.Should().Be(normalWidth);
    }

    [Fact]
    public void A_long_loading_message_does_not_overwrite_the_volume_readout()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);
        panel.Update(Snap(0, 600_000, playing: false), volume0to100: 42, Fmt);
        tui.Render();

        panel.SetLoading(true, "still connecting… check your network");
        tui.Render();

        tui.ScreenContains("42%").Should().BeTrue();
        tui.ScreenContains("Vol+").Should().BeTrue();
    }

    // ── title ───────────────────────────────────────────────────────────────

    [Fact]
    public void Shows_the_episode_title()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.TitleLabel.Text = "Some Episode";
        tui.Render();

        tui.ScreenContains("Some Episode").Should().BeTrue();
    }

    // ── glyphs ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_play_button_stays_ascii_when_unicode_is_off()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Ascii);
        try
        {
            using var tui = new TuiHarness();
            var panel = Mount(tui);

            panel.Update(Snap(1_000, 600_000, playing: true), 50, Fmt);
            panel.OptimisticToggle();
            tui.Render();

            var text = panel.BtnPlayPause.Text?.ToString() ?? "";
            text.Should().NotContain("⏵");
            text.Should().NotContain("⏸");
        }
        finally
        {
            UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        }
    }

    [Fact]
    public void OptimisticToggle_offers_the_next_action_not_the_current_state()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        // While playing, pressing space pauses, so the button has to offer
        // "Play" for the press after that.
        panel.Update(Snap(1_000, 600_000, playing: true), 50, Fmt);
        panel.OptimisticToggle();

        (panel.BtnPlayPause.Text?.ToString() ?? "").Should().Contain("Play");
    }

    [Fact]
    public void OptimisticToggle_offers_pause_when_paused()
    {
        UIGlyphSet.Use(UIGlyphSet.Profile.Unicode);
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        panel.Update(Snap(1_000, 600_000, playing: false), 50, Fmt);
        panel.OptimisticToggle();

        (panel.BtnPlayPause.Text?.ToString() ?? "").Should().Contain("Pause");
    }

    // ── width ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_control_is_visible_on_a_wide_terminal()
    {
        using var tui = new TuiHarness(cols: 160, rows: 12);
        var panel = Mount(tui);
        panel.Update(Snap(0, 600_000, playing: false), 42, Fmt);
        tui.Render();

        var row = tui.Line(tui.RowOf("Vol+"));
        row.Should().Contain("10s»");
        row.Should().Contain("Download");
        row.Should().Contain("-spd");
        row.Should().Contain("+spd");
        row.Should().Contain("Vol−");
    }

    // ── robustness ──────────────────────────────────────────────────────────

    [Fact]
    public void Update_never_throws_on_odd_values()
    {
        using var tui = new TuiHarness();
        var panel = Mount(tui);

        var act = () =>
        {
            panel.Update(Snap(0, 0, false), 0, Fmt);
            panel.Update(Snap(5_000, 1_000, true), 100, Fmt);   // position past length
            panel.Update(Snap(0, 600_000, true, speed: 0), 50, Fmt);
        };

        act.Should().NotThrow();
    }
}
