using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.App.UI.Wiring;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// Removing a feed and refreshing all of them. The refresh path carries the
// only real branching in the file: failures are collected during the pass and
// summarised once at the end, because OSDing every failing feed floods the UI
// when the whole network is down.
public sealed class UiFeedWiringTests
{
    private static readonly Guid FeedA = Guid.Parse("aaaa1111-0000-0000-0000-00000000000a");
    private static readonly Guid FeedB = Guid.Parse("bbbb2222-0000-0000-0000-00000000000b");

    private static Feed Fd(Guid id, string title) => new() { Id = id, Title = title, Url = $"https://ex.test/{title}.xml" };

    private static Episode Ep(Guid feed, string title) => new()
    {
        Id = Guid.NewGuid(), FeedId = feed, Title = title, AudioUrl = "https://ex.test/a.mp3",
    };

    private sealed class Fixture
    {
        public readonly FakeUiShell Ui = new();
        public readonly AppData Data = new();
        public readonly FakeFeedService Feeds = new();
        public readonly FakeFeedStore FeedStore = new();
        public readonly FakeEpisodeStore Episodes = new();
        public readonly ViewUseCase View;

        public Fixture()
        {
            View = new ViewUseCase(Ui, Data, () => Task.CompletedTask, Episodes, FeedStore);
            UiFeedWiring.WireRemoveFeed(Ui, Feeds, FeedStore, Episodes);
            UiFeedWiring.WireRefresh(Ui, Data, Feeds, FeedStore, Episodes, View);
        }

        public string? LastOsd => Ui.OsdMessages.LastOrDefault().Text;
    }

    // ── removing a feed ─────────────────────────────────────────────────────

    [Fact]
    public void Removing_with_no_feed_selected_says_so()
    {
        var f = new Fixture();
        f.Ui.SelectedFeedId = null;

        f.Ui.RaiseRemoveFeedRequested();

        f.Feeds.Removals.Should().BeEmpty();
        f.LastOsd.Should().Contain("no feed selected");
    }

    [Fact]
    public void Removing_asks_the_feed_service()
    {
        var f = new Fixture();
        f.FeedStore.Seed(Fd(FeedA, "A"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseRemoveFeedRequested();

        f.Feeds.Removals.Should().ContainSingle().Which.FeedId.Should().Be(FeedA);
    }

    [Fact]
    public void Removing_confirms_on_the_osd()
    {
        var f = new Fixture();
        f.FeedStore.Seed(Fd(FeedA, "A"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseRemoveFeedRequested();

        f.LastOsd.Should().Contain("feed removed");
    }

    [Fact]
    public void Removing_surfaces_a_failure_instead_of_swallowing_it()
    {
        var f = new Fixture();
        f.FeedStore.Seed(Fd(FeedA, "A"));
        f.Ui.SelectedFeedId = FeedA;
        f.Feeds.ThrowOnRemove = new InvalidOperationException("disk on fire");

        f.Ui.RaiseRemoveFeedRequested();

        f.LastOsd.Should().Contain("remove failed");
        f.LastOsd.Should().Contain("disk on fire");
    }

    // ── refreshing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Refreshing_while_offline_fetches_nothing()
    {
        var f = new Fixture();
        f.Data.NetworkOnline = false;

        await f.Ui.RaiseRefreshRequested();

        f.Feeds.RefreshAllCalls.Should().Be(0);
        f.LastOsd.Should().Contain("offline");
    }

    [Fact]
    public async Task Refreshing_online_runs_a_pass()
    {
        var f = new Fixture();
        f.Data.NetworkOnline = true;

        await f.Ui.RaiseRefreshRequested();

        f.Feeds.RefreshAllCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_clean_refresh_just_confirms()
    {
        var f = new Fixture();
        f.Data.NetworkOnline = true;

        await f.Ui.RaiseRefreshRequested();

        f.LastOsd.Should().Contain("refreshed");
    }

    [Fact]
    public async Task One_failing_feed_shows_its_reason()
    {
        var f = new Fixture();
        f.Data.NetworkOnline = true;
        f.Feeds.FailuresToRaise.Add((Fd(FeedA, "Darknet Diaries"), "HTTP 404"));

        await f.Ui.RaiseRefreshRequested();

        f.LastOsd.Should().Contain("Darknet Diaries");
        f.LastOsd.Should().Contain("HTTP 404");
    }

    [Fact]
    public async Task Several_failing_feeds_show_only_a_count()
    {
        // The whole network being down must not produce one OSD per feed.
        var f = new Fixture();
        f.Data.NetworkOnline = true;
        f.Feeds.FailuresToRaise.Add((Fd(FeedA, "A"), "timed out"));
        f.Feeds.FailuresToRaise.Add((Fd(FeedB, "B"), "timed out"));
        f.Feeds.FailuresToRaise.Add((Fd(Guid.NewGuid(), "C"), "timed out"));

        await f.Ui.RaiseRefreshRequested();

        f.LastOsd.Should().Contain("3 feeds failed");
        f.LastOsd.Should().NotContain("timed out");
    }

    [Fact]
    public async Task Failures_do_not_carry_over_into_the_next_refresh()
    {
        var f = new Fixture();
        f.Data.NetworkOnline = true;
        f.Feeds.FailuresToRaise.Add((Fd(FeedA, "A"), "HTTP 500"));
        await f.Ui.RaiseRefreshRequested();

        f.Feeds.FailuresToRaise.Clear();
        await f.Ui.RaiseRefreshRequested();

        f.LastOsd.Should().Contain("refreshed");
        f.LastOsd.Should().NotContain("HTTP 500");
    }

    [Fact]
    public async Task Refreshing_re_renders_the_selected_feed()
    {
        var f = new Fixture();
        f.Data.NetworkOnline = true;
        f.FeedStore.Seed(Fd(FeedA, "A"));
        f.Episodes.Seed(Ep(FeedA, "One"));
        f.Ui.SelectedFeedId = FeedA;

        await f.Ui.RaiseRefreshRequested();

        f.Ui.SetEpisodeCalls.Should().NotBeEmpty();
        f.Ui.SetEpisodeCalls.Last().Item1.Should().Be(FeedA);
    }
}
