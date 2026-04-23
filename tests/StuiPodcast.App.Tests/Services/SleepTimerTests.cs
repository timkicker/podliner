using FluentAssertions;
using StuiPodcast.App.Services;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class SleepTimerTests
{
    // ── ParseDuration ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("30m",     30 * 60)]
    [InlineData("1h",      60 * 60)]
    [InlineData("90s",     90)]
    [InlineData("1h30m",   60 * 60 + 30 * 60)]
    [InlineData("0.5h",    30 * 60)]
    [InlineData("1.5m",    90)]
    [InlineData("1h30m45s", 60 * 60 + 30 * 60 + 45)]
    public void ParseDuration_accepts_standard_forms(string input, double expectedSeconds)
    {
        var parsed = SleepTimer.ParseDuration(input);
        parsed.Should().NotBeNull();
        parsed!.Value.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.01);
    }

    [Fact]
    public void ParseDuration_bare_number_is_minutes()
    {
        SleepTimer.ParseDuration("30")!.Value.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("30x")]       // unknown unit
    [InlineData("m30")]       // unit before number
    [InlineData("0")]          // non-positive
    [InlineData("-5m")]        // negative (parser only accepts digits)
    public void ParseDuration_rejects_bad_input(string? input)
    {
        SleepTimer.ParseDuration(input).Should().BeNull();
    }

    [Fact]
    public void ParseDuration_handles_uppercase()
    {
        SleepTimer.ParseDuration("1H30M")!.Value.TotalMinutes.Should().Be(90);
    }

    // ── FormatDuration ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(30,    "30s")]
    [InlineData(60,    "1m")]
    [InlineData(90,    "1m 30s")]
    [InlineData(3600,  "1h")]
    [InlineData(3661,  "1h 1m 1s")]
    [InlineData(7200,  "2h")]
    public void FormatDuration_omits_zero_units(int totalSeconds, string expected)
    {
        SleepTimer.FormatDuration(TimeSpan.FromSeconds(totalSeconds)).Should().Be(expected);
    }

    [Fact]
    public void FormatDuration_sub_second_becomes_zero()
    {
        SleepTimer.FormatDuration(TimeSpan.FromMilliseconds(400)).Should().Be("0s");
    }

    // ── Set/Cancel ───────────────────────────────────────────────────────────
    // Can't easily test the fire path without a Terminal.Gui main loop,
    // but we can verify that Set without a main loop doesn't throw and
    // that state reflects what was set.

    [Fact]
    public void Set_without_mainloop_does_not_throw()
    {
        var timer = new SleepTimer(() => { });
        var act = () => timer.Set(TimeSpan.FromMinutes(5));
        act.Should().NotThrow();
    }

    [Fact]
    public void Cancel_without_active_timer_is_noop()
    {
        var timer = new SleepTimer(() => { });
        var act = () => timer.Cancel();
        act.Should().NotThrow();
        timer.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Set_with_zero_or_negative_duration_is_ignored()
    {
        var timer = new SleepTimer(() => { });
        timer.Set(TimeSpan.Zero);
        timer.IsActive.Should().BeFalse();
        timer.Set(TimeSpan.FromSeconds(-5));
        timer.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Constructor_rejects_null_callback()
    {
        var act = () => new SleepTimer(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
