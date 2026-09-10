using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.App.UI.Wiring;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// What happens when the user moves between feeds or episodes: the list is
// re-rendered for the new feed, the selection is remembered for the next
// launch, and the change is persisted. First coverage of anything in
// UI/Wiring; the Wire overload takes only the three things it uses so it can
// run without the whole composition root.
public sealed class UiSelectionWiringTests
{
    private static readonly Guid FeedId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class Fixture
    {
        public readonly FakeUiShell Ui = new();
        public readonly AppData Data = new();
        public readonly FakeEpisodeStore Episodes = new();
        public int Saves;

        public Fixture()
        {
            UiSelectionWiring.Wire(Ui, Data, Episodes, () => { Saves++; return Task.CompletedTask; });
        }
    }

    private static Episode Ep(Guid feed, string title) => new()
    {
        Id = Guid.NewGuid(),
        FeedId = feed,
        Title = title,
        AudioUrl = "https://ex.test/a.mp3",
    };

    // ── feed selection ──────────────────────────────────────────────────────

    [Fact]
    public void Choosing_a_feed_remembers_it()
    {
        var f = new Fixture();
        f.Ui.SelectedFeedId = FeedId;

        f.Ui.RaiseSelectedFeedChanged();

        f.Data.LastSelectedFeedId.Should().Be(FeedId);
    }

    [Fact]
    public void Choosing_a_feed_renders_its_episodes()
    {
        var f = new Fixture();
        f.Episodes.Seed(Ep(FeedId, "One"), Ep(FeedId, "Two"));
        f.Ui.SelectedFeedId = FeedId;

        f.Ui.RaiseSelectedFeedChanged();

        f.Ui.SetEpisodeCalls.Should().ContainSingle();
        f.Ui.SetEpisodeCalls[0].Item1.Should().Be(FeedId);
    }

    [Fact]
    public void Choosing_a_feed_moves_the_cursor_to_the_top()
    {
        var f = new Fixture();
        f.Ui.SelectedFeedId = FeedId;

        f.Ui.RaiseSelectedFeedChanged();

        f.Ui.LastSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Choosing_a_feed_persists()
    {
        var f = new Fixture();
        f.Ui.SelectedFeedId = FeedId;

        f.Ui.RaiseSelectedFeedChanged();

        f.Saves.Should().Be(1);
    }

    [Fact]
    public void Landing_on_no_feed_clears_the_remembered_one()
    {
        // The barrier row between virtual and real feeds reports null.
        var f = new Fixture();
        f.Data.LastSelectedFeedId = FeedId;
        f.Ui.SelectedFeedId = null;

        f.Ui.RaiseSelectedFeedChanged();

        f.Data.LastSelectedFeedId.Should().BeNull();
    }

    [Fact]
    public void Landing_on_no_feed_does_not_render_a_list()
    {
        var f = new Fixture();
        f.Ui.SelectedFeedId = null;

        f.Ui.RaiseSelectedFeedChanged();

        f.Ui.SetEpisodeCalls.Should().BeEmpty();
    }

    [Fact]
    public void Landing_on_no_feed_still_persists()
    {
        var f = new Fixture();
        f.Ui.SelectedFeedId = null;

        f.Ui.RaiseSelectedFeedChanged();

        f.Saves.Should().Be(1);
    }

    [Fact]
    public void Switching_between_feeds_re_renders_each_time()
    {
        var f = new Fixture();
        var other = Guid.NewGuid();

        f.Ui.SelectedFeedId = FeedId;
        f.Ui.RaiseSelectedFeedChanged();
        f.Ui.SelectedFeedId = other;
        f.Ui.RaiseSelectedFeedChanged();

        f.Ui.SetEpisodeCalls.Should().HaveCount(2);
        f.Ui.SetEpisodeCalls[1].Item1.Should().Be(other);
        f.Data.LastSelectedFeedId.Should().Be(other);
    }

    // ── episode selection ───────────────────────────────────────────────────

    [Fact]
    public void Moving_between_episodes_persists()
    {
        var f = new Fixture();

        f.Ui.RaiseEpisodeSelectionChanged();

        f.Saves.Should().Be(1);
    }

    [Fact]
    public void Moving_between_episodes_does_not_rebuild_the_list()
    {
        // Re-rendering on every j/k would rebuild every row per keypress.
        var f = new Fixture();

        f.Ui.RaiseEpisodeSelectionChanged();

        f.Ui.SetEpisodeCalls.Should().BeEmpty();
    }

    [Fact]
    public void Every_selection_change_persists_once()
    {
        var f = new Fixture();

        for (int i = 0; i < 5; i++) f.Ui.RaiseEpisodeSelectionChanged();

        f.Saves.Should().Be(5);
    }
}
