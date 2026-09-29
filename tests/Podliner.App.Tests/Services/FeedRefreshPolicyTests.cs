using FluentAssertions;
using Podliner.App.Services;
using Xunit;

namespace Podliner.App.Tests.Services;

// #32: feeds were only fetched when someone typed :refresh, so the library
// sat on months-old episodes with nothing on screen to say so. The policy
// decides when a refresh is due and how stale the sidebar says it is.
public sealed class FeedRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    // ── when is a refresh due ────────────────────────────────────────────────

    [Fact]
    public void Never_refreshed_is_due_at_once()
    {
        FeedRefreshPolicy.IsDue(Now, lastAttempt: null, intervalMinutes: 60, online: true, running: false)
            .Should().BeTrue("this is the reporter's case: an upgrade with nothing on record");
    }

    [Fact]
    public void A_refresh_inside_the_interval_is_not_due_yet()
    {
        FeedRefreshPolicy.IsDue(Now, Now.AddMinutes(-59), 60, online: true, running: false)
            .Should().BeFalse();
    }

    [Fact]
    public void A_refresh_exactly_one_interval_ago_is_due()
    {
        FeedRefreshPolicy.IsDue(Now, Now.AddMinutes(-60), 60, online: true, running: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Off_means_never()
    {
        FeedRefreshPolicy.IsDue(Now, lastAttempt: null, intervalMinutes: 0, online: true, running: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Offline_is_never_due()
    {
        FeedRefreshPolicy.IsDue(Now, lastAttempt: null, 60, online: false, running: false)
            .Should().BeFalse("every feed would fail and the pass would only count failures");
    }

    [Fact]
    public void A_pass_already_running_is_not_started_twice()
    {
        FeedRefreshPolicy.IsDue(Now, lastAttempt: null, 60, online: true, running: true)
            .Should().BeFalse();
    }

    [Fact]
    public void A_clock_that_went_backwards_does_not_wait_until_it_catches_up()
    {
        // A last-attempt in the future (clock change, restored backup) must
        // not suspend refreshing until that moment comes round again.
        FeedRefreshPolicy.IsDue(Now, Now.AddDays(3), 60, online: true, running: false)
            .Should().BeTrue();
    }

    // ── interval ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    [InlineData(10080, 10080)]
    [InlineData(99999, 10080)]
    public void Intervals_are_off_or_between_five_minutes_and_a_week(int given, int expected)
    {
        FeedRefreshPolicy.Normalize(given).Should().Be(expected);
    }

    [Fact]
    public void The_default_is_an_hour()
    {
        FeedRefreshPolicy.DefaultIntervalMinutes.Should().Be(60);
    }

    // ── how stale does it look ───────────────────────────────────────────────

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(59 * 60, "59m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(23 * 3600, "23h ago")]
    [InlineData(24 * 3600, "1d ago")]
    [InlineData(29 * 86400, "29d ago")]
    [InlineData(30 * 86400, "1mo ago")]
    [InlineData(140 * 86400, "4mo ago")]
    [InlineData(400 * 86400, "1y ago")]
    public void Ages_read_like_a_person_would_say_them(int seconds, string expected)
    {
        FeedRefreshPolicy.FormatAge(Now.AddSeconds(-seconds), Now).Should().Be(expected);
    }

    [Fact]
    public void Nothing_on_record_reads_never()
    {
        FeedRefreshPolicy.FormatAge(null, Now).Should().Be("never");
    }

    [Fact]
    public void A_future_timestamp_reads_just_now_rather_than_a_negative_age()
    {
        FeedRefreshPolicy.FormatAge(Now.AddMinutes(10), Now).Should().Be("just now");
    }

    // ── the sidebar title ────────────────────────────────────────────────────

    [Fact]
    public void The_sidebar_says_how_old_the_feeds_are()
    {
        FeedRefreshPolicy.SidebarTitle(Now.AddMinutes(-12), Now, failed: 0, separator: "·")
            .Should().Be("Feeds · 12m ago");
    }

    [Fact]
    public void Failed_feeds_are_counted_in_the_title()
    {
        FeedRefreshPolicy.SidebarTitle(Now.AddMinutes(-12), Now, failed: 2, separator: "·")
            .Should().Be("Feeds · 12m · 2 failed");
    }

    [Theory]
    [InlineData(400 * 86400, 0)]
    [InlineData(400 * 86400, 5)]
    [InlineData(330 * 86400, 999)]
    [InlineData(330 * 86400, 100000)]
    [InlineData(0, 100000)]
    public void The_title_fits_the_sidebar(int ageSeconds, int failed)
    {
        // Terminal.Gui cuts a frame title of the 30 column sidebar at 24
        // characters; measured, "Feeds · 11mo · 999+ failed" came out as
        // "Feeds · 11mo · 999+ fail".
        FeedRefreshPolicy.SidebarTitle(Now.AddSeconds(-ageSeconds), Now, failed, "·")
            .Length.Should().BeLessThanOrEqualTo(FeedRefreshPolicy.MaxTitleLength);
    }

    [Fact]
    public void When_age_and_failures_do_not_both_fit_the_failures_win()
    {
        // A pile of failures is the thing to notice; the age is only context.
        FeedRefreshPolicy.SidebarTitle(Now.AddDays(-330), Now, failed: 5000, separator: "·")
            .Should().Be("Feeds · 99+ failed");
    }

    [Fact]
    public void The_ascii_separator_is_used_when_asked()
    {
        FeedRefreshPolicy.SidebarTitle(Now.AddMinutes(-5), Now, failed: 0, separator: "|")
            .Should().Be("Feeds | 5m ago");
    }
}
