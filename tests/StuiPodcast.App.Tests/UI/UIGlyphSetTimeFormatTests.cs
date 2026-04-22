using FluentAssertions;
using StuiPodcast.App.UI;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// Regression tests for the time-display bugs that caused the player bar
// and episode list to disagree. Previously UiShell used a local formatter
// based on TimeSpan.TotalMinutes which produced "120:00" for a 2h episode
// while UIGlyphSet.FormatDuration produced "2:00:00". UIGlyphSet.FormatTime
// is now the single source of truth.
public sealed class UIGlyphSetTimeFormatTests
{
    // ── FormatTime(TimeSpan) ─────────────────────────────────────────────────

    [Fact]
    public void Zero_time_formats_as_00_00()
    {
        UIGlyphSet.FormatTime(TimeSpan.Zero).Should().Be("00:00");
    }

    [Fact]
    public void Negative_time_clamps_to_zero()
    {
        UIGlyphSet.FormatTime(TimeSpan.FromSeconds(-5)).Should().Be("00:00");
    }

    [Theory]
    [InlineData(0, 45, "00:45")]
    [InlineData(1, 30, "01:30")]
    [InlineData(59, 59, "59:59")]
    public void Sub_hour_uses_mm_ss(int minutes, int seconds, string expected)
    {
        UIGlyphSet.FormatTime(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(1,  0,  0, "1:00:00")]
    [InlineData(2,  0,  0, "2:00:00")]
    [InlineData(1, 15, 30, "1:15:30")]
    [InlineData(3,  5,  7, "3:05:07")]
    [InlineData(10, 0,  0, "10:00:00")]
    public void Multi_hour_uses_h_mm_ss(int h, int m, int s, string expected)
    {
        var t = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
        UIGlyphSet.FormatTime(t).Should().Be(expected);
    }

    [Fact]
    public void Two_hour_episode_does_not_show_120_minutes()
    {
        // This is the original bug: a 2h podcast was shown as "120:00".
        UIGlyphSet.FormatTime(TimeSpan.FromHours(2)).Should().NotContain("120");
        UIGlyphSet.FormatTime(TimeSpan.FromHours(2)).Should().Be("2:00:00");
    }

    [Fact]
    public void Fractional_seconds_truncate()
    {
        // Floor semantics: 1.9s → 00:01, not 00:02. Matches audio-player
        // convention where we don't show "almost the next second".
        UIGlyphSet.FormatTime(TimeSpan.FromMilliseconds(1900)).Should().Be("00:01");
        UIGlyphSet.FormatTime(TimeSpan.FromMilliseconds(59999)).Should().Be("00:59");
    }

    // ── FormatDuration(long ms) still consistent with FormatTime ─────────────

    [Fact]
    public void FormatDuration_and_FormatTime_agree_for_same_input()
    {
        long[] tests = { 0, 1000, 45_000, 3_600_000, 7_200_000, 12_345_000 };
        foreach (var ms in tests)
        {
            if (ms <= 0) continue;
            var viaDuration = UIGlyphSet.FormatDuration(ms);
            var viaTime = UIGlyphSet.FormatTime(TimeSpan.FromMilliseconds(ms));
            viaTime.Should().Be(viaDuration,
                $"player bar and episode list must show the same string for the same duration ({ms}ms)");
        }
    }

    [Fact]
    public void FormatDuration_zero_shows_placeholder()
    {
        UIGlyphSet.FormatDuration(0).Should().Be("--:--");
        UIGlyphSet.FormatDuration(-100).Should().Be("--:--");
    }
}
