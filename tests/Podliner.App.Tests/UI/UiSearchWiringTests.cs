using FluentAssertions;
using Podliner.App.Tests.Fakes;
using Podliner.App.UI.Wiring;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.UI;

// What `/` actually does: filter the episodes of the selected feed by title
// or shownotes. Live-typed, so it runs on every keystroke.
public sealed class UiSearchWiringTests
{
    private static readonly Guid FeedA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid FeedB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private sealed class Fixture
    {
        public readonly FakeUiShell Ui = new();
        public readonly FakeEpisodeStore Episodes = new();

        public Fixture(params Episode[] seed)
        {
            Episodes.Seed(seed);
            UiCommandWiring.WireSearch(Ui, Episodes);
        }

        // Titles the search produced for the list, in order.
        public IReadOnlyList<string> Result
            => Ui.SetEpisodeCalls.Last().Item2.Select(e => e.Title).ToList();

        public bool Rendered => Ui.SetEpisodeCalls.Count > 0;
    }

    private static Episode Ep(Guid feed, string title, string notes = "")
        => new()
        {
            Id = Guid.NewGuid(), FeedId = feed, Title = title,
            AudioUrl = "https://ex.test/a.mp3", DescriptionText = notes,
        };

    // ── filtering ───────────────────────────────────────────────────────────

    [Fact]
    public void A_query_keeps_only_matching_titles()
    {
        var f = new Fixture(Ep(FeedA, "Rust Talk"), Ep(FeedA, "Go Talk"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("rust");

        f.Result.Should().Equal("Rust Talk");
    }

    [Fact]
    public void A_query_also_searches_the_shownotes()
    {
        var f = new Fixture(
            Ep(FeedA, "Episode One", notes: "we talk about borrow checkers"),
            Ep(FeedA, "Episode Two", notes: "unrelated"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("borrow");

        f.Result.Should().Equal("Episode One");
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var f = new Fixture(Ep(FeedA, "Rust Talk"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("RUST");

        f.Result.Should().Equal("Rust Talk");
    }

    [Fact]
    public void A_partial_word_matches()
    {
        var f = new Fixture(Ep(FeedA, "Kubernetes Deep Dive"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("bernet");

        f.Result.Should().ContainSingle();
    }

    [Fact]
    public void A_query_that_matches_nothing_empties_the_list()
    {
        var f = new Fixture(Ep(FeedA, "Rust Talk"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("nothing here");

        f.Result.Should().BeEmpty();
    }

    // ── scope ───────────────────────────────────────────────────────────────

    [Fact]
    public void Search_stays_inside_the_selected_feed()
    {
        var f = new Fixture(Ep(FeedA, "Talk A"), Ep(FeedB, "Talk B"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("talk");

        f.Result.Should().Equal("Talk A");
    }

    [Fact]
    public void With_no_feed_selected_nothing_is_rendered()
    {
        // Nowhere to put the result, so the handler does not call back.
        var f = new Fixture(Ep(FeedA, "Talk A"));
        f.Ui.SelectedFeedId = null;

        f.Ui.RaiseSearchApplied("talk");

        f.Rendered.Should().BeFalse();
    }

    // ── clearing the query ──────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_query_restores_the_whole_feed(string query)
    {
        var f = new Fixture(Ep(FeedA, "One"), Ep(FeedA, "Two"), Ep(FeedB, "Other"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied(query);

        f.Result.Should().BeEquivalentTo(new[] { "One", "Two" });
    }

    [Fact]
    public void Typing_and_then_clearing_gets_everything_back()
    {
        var f = new Fixture(Ep(FeedA, "One"), Ep(FeedA, "Two"));
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("one");
        f.Result.Should().ContainSingle();

        f.Ui.RaiseSearchApplied("");
        f.Result.Should().HaveCount(2);
    }

    [Fact]
    public void Every_keystroke_re_renders()
    {
        var f = new Fixture(Ep(FeedA, "Rust Talk"));
        f.Ui.SelectedFeedId = FeedA;

        foreach (var q in new[] { "r", "ru", "rus", "rust" })
            f.Ui.RaiseSearchApplied(q);

        f.Ui.SetEpisodeCalls.Should().HaveCount(4);
    }

    // ── robustness ──────────────────────────────────────────────────────────

    [Fact]
    public void An_episode_without_shownotes_does_not_break_the_search()
    {
        var f = new Fixture(Ep(FeedA, "Titled", notes: ""));
        f.Ui.SelectedFeedId = FeedA;

        var act = () => f.Ui.RaiseSearchApplied("titled");

        act.Should().NotThrow();
        f.Result.Should().ContainSingle();
    }

    [Fact]
    public void Searching_an_empty_library_is_harmless()
    {
        var f = new Fixture();
        f.Ui.SelectedFeedId = FeedA;

        f.Ui.RaiseSearchApplied("anything");

        f.Result.Should().BeEmpty();
    }
}
