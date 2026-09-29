using FluentAssertions;
using Podliner.App.Services;
using Podliner.App.Tests.Fakes;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.Services;

// #32: nothing fetched feeds unless someone typed :refresh. FeedRefresher
// runs a pass when one is due, remembers when a pass last got anything
// through, and counts what failed, for the timer and for :refresh alike.
public sealed class FeedRefresherTests
{
    private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeFeedService _feeds = new();
    private readonly FakeFeedStore _store = new();
    private readonly AppData _data = new() { NetworkOnline = true, RefreshIntervalMinutes = 60 };
    private int _saves;

    private FeedRefresher Make()
        => new(_feeds, _store, _data, () => { _saves++; return Task.CompletedTask; }, () => _now);

    private static Feed Fd(string title) => new() { Id = Guid.NewGuid(), Title = title, Url = $"https://ex.test/{title}.xml" };

    // ── when a pass runs ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_library_never_refreshed_gets_a_pass_on_the_first_tick()
    {
        _store.Seed(Fd("a"));
        var r = Make();

        var outcome = await r.TickAsync();

        outcome.Result.Should().Be(RefreshResult.Ran);
        _feeds.RefreshAllCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_pass_inside_the_interval_is_not_repeated()
    {
        _store.Seed(Fd("a"));
        var r = Make();
        await r.TickAsync();

        _now = _now.AddMinutes(59);
        var outcome = await r.TickAsync();

        outcome.Result.Should().Be(RefreshResult.NotDue);
        _feeds.RefreshAllCalls.Should().Be(1);
    }

    [Fact]
    public async Task The_next_pass_comes_when_the_interval_is_up()
    {
        _store.Seed(Fd("a"));
        var r = Make();
        await r.TickAsync();

        _now = _now.AddMinutes(60);
        await r.TickAsync();

        _feeds.RefreshAllCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_recent_pass_from_before_a_restart_counts()
    {
        // Restarting podliner five times in a minute must not fetch every
        // feed five times.
        _store.Seed(Fd("a"));
        _data.LastRefreshAt = _now.AddMinutes(-10);
        var r = Make();

        var outcome = await r.TickAsync();

        outcome.Result.Should().Be(RefreshResult.NotDue);
        _feeds.RefreshAllCalls.Should().Be(0);
    }

    [Fact]
    public async Task Nothing_runs_while_offline()
    {
        _store.Seed(Fd("a"));
        _data.NetworkOnline = false;

        (await Make().TickAsync()).Result.Should().Be(RefreshResult.NotDue);
        _feeds.RefreshAllCalls.Should().Be(0);
    }

    [Fact]
    public async Task Nothing_runs_when_switched_off()
    {
        _store.Seed(Fd("a"));
        _data.RefreshIntervalMinutes = 0;

        (await Make().TickAsync()).Result.Should().Be(RefreshResult.NotDue);
        _feeds.RefreshAllCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_manual_pass_runs_even_when_switched_off()
    {
        // :refresh is someone asking; the interval only governs the timer.
        _store.Seed(Fd("a"));
        _data.RefreshIntervalMinutes = 0;

        (await Make().RunAsync()).Result.Should().Be(RefreshResult.Ran);
        _feeds.RefreshAllCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_second_pass_is_not_started_while_one_is_running()
    {
        _store.Seed(Fd("a"));
        _feeds.HoldRefreshAll = new TaskCompletionSource();
        var r = Make();

        var first = r.RunAsync();
        var second = await r.RunAsync();

        second.Result.Should().Be(RefreshResult.Skipped);
        r.IsRunning.Should().BeTrue();

        _feeds.HoldRefreshAll.SetResult();
        (await first).Result.Should().Be(RefreshResult.Ran);
        r.IsRunning.Should().BeFalse();
        _feeds.RefreshAllCalls.Should().Be(1);
    }

    // ── what a pass leaves behind ────────────────────────────────────────────

    [Fact]
    public async Task A_clean_pass_records_the_time_and_saves_it()
    {
        _store.Seed(Fd("a"));
        var r = Make();

        await r.RunAsync();

        _data.LastRefreshAt.Should().Be(_now);
        _saves.Should().BeGreaterThan(0);
        r.LastFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task One_failing_feed_out_of_several_still_counts_as_refreshed()
    {
        var a = Fd("a"); var b = Fd("b"); var c = Fd("c");
        _store.Seed(a, b, c);
        _feeds.FailuresToRaise.Add((b, "HTTP 404"));
        var r = Make();

        var outcome = await r.RunAsync();

        _data.LastRefreshAt.Should().Be(_now, "two of three feeds came through");
        r.LastFailedCount.Should().Be(1);
        outcome.Failures.Should().ContainSingle().Which.Should().Contain("HTTP 404");
    }

    [Fact]
    public async Task A_pass_where_every_feed_failed_does_not_claim_the_feeds_are_fresh()
    {
        var before = _now.AddHours(-5);
        _data.LastRefreshAt = before;
        var a = Fd("a"); var b = Fd("b");
        _store.Seed(a, b);
        _feeds.FailuresToRaise.Add((a, "timed out"));
        _feeds.FailuresToRaise.Add((b, "timed out"));
        _now = _now.AddHours(1);
        var r = Make();

        await r.RunAsync();

        _data.LastRefreshAt.Should().Be(before, "nothing came through, so the sidebar has to keep showing the age");
        r.LastFailedCount.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_pass_is_not_retried_on_every_tick()
    {
        var a = Fd("a");
        _store.Seed(a);
        _feeds.FailuresToRaise.Add((a, "timed out"));
        var r = Make();
        await r.TickAsync();

        _now = _now.AddMinutes(1);
        await r.TickAsync();

        _feeds.RefreshAllCalls.Should().Be(1, "a server that is down would otherwise be hit twice a minute");
    }

    [Fact]
    public async Task A_pass_that_throws_counts_every_feed_as_failed_and_does_not_escape()
    {
        _store.Seed(Fd("a"), Fd("b"));
        _feeds.ThrowOnRefreshAll = new HttpRequestException("network gone");
        var r = Make();

        var outcome = await r.RunAsync();

        outcome.Result.Should().Be(RefreshResult.Ran);
        r.LastFailedCount.Should().Be(2);
        _data.LastRefreshAt.Should().BeNull();
        r.IsRunning.Should().BeFalse("a throw must not leave the guard set for ever");
    }

    [Fact]
    public async Task Failures_do_not_carry_over_into_the_next_pass()
    {
        var a = Fd("a");
        _store.Seed(a);
        _feeds.FailuresToRaise.Add((a, "HTTP 500"));
        var r = Make();
        await r.RunAsync();

        _feeds.FailuresToRaise.Clear();
        var outcome = await r.RunAsync();

        outcome.Failures.Should().BeEmpty();
        r.LastFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task Every_pass_announces_that_it_finished()
    {
        _store.Seed(Fd("a"));
        var r = Make();
        var finished = 0;
        r.Finished += () => finished++;

        await r.RunAsync();
        await r.RunAsync();

        finished.Should().Be(2);
    }

    // ── what the sidebar says ────────────────────────────────────────────────

    [Fact]
    public void With_no_feeds_the_title_is_just_Feeds()
    {
        Make().SidebarTitle("·").Should().Be("Feeds");
    }

    [Fact]
    public void Never_refreshed_says_so()
    {
        _store.Seed(Fd("a"));
        Make().SidebarTitle("·").Should().Be("Feeds · never");
    }

    [Fact]
    public async Task After_a_pass_the_title_shows_the_age()
    {
        _store.Seed(Fd("a"));
        var r = Make();
        await r.RunAsync();

        _now = _now.AddMinutes(12);

        r.SidebarTitle("·").Should().Be("Feeds · 12m ago");
    }

    [Fact]
    public async Task After_failures_the_title_counts_them()
    {
        var a = Fd("a"); var b = Fd("b");
        _store.Seed(a, b);
        _feeds.FailuresToRaise.Add((a, "HTTP 404"));
        var r = Make();
        await r.RunAsync();

        r.SidebarTitle("·").Should().Be("Feeds · now · 1 failed");
    }

    // ── while a pass runs ────────────────────────────────────────────────────
    //
    // Measured with a real library: two feeds timing out at 30s each kept the
    // first pass going for over a minute, and all that time the sidebar said
    // "never".

    [Fact]
    public async Task While_a_pass_runs_the_title_says_so()
    {
        _store.Seed(Fd("a"));
        _feeds.HoldRefreshAll = new TaskCompletionSource();
        var r = Make();

        var pass = r.RunAsync();

        r.SidebarTitle("·").Should().Be("Feeds · refreshing");
        _feeds.HoldRefreshAll.SetResult();
        await pass;
        r.SidebarTitle("·").Should().Be("Feeds · just now");
    }

    [Fact]
    public async Task A_pass_announces_that_it_started()
    {
        _store.Seed(Fd("a"));
        var r = Make();
        var titlesAtStart = new List<string>();
        r.Started += () => titlesAtStart.Add(r.SidebarTitle("·"));

        await r.RunAsync();

        titlesAtStart.Should().Equal("Feeds · refreshing");
    }

    [Fact]
    public async Task A_skipped_pass_does_not_announce_a_start()
    {
        _store.Seed(Fd("a"));
        _feeds.HoldRefreshAll = new TaskCompletionSource();
        var r = Make();
        var starts = 0;
        r.Started += () => starts++;

        var first = r.RunAsync();
        await r.RunAsync();

        starts.Should().Be(1);
        _feeds.HoldRefreshAll.SetResult();
        await first;
    }
}
