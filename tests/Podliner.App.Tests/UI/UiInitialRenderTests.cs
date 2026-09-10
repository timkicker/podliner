using FluentAssertions;
using Podliner.App.Tests.Fakes;
using Podliner.App.UI.Wiring;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.UI;

// Which episode podliner opens on. Picking wrong is the kind of thing nobody
// files a bug about, they just find the app annoying.
public sealed class UiInitialRenderTests
{
    private static readonly Guid FeedId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static Episode Ep(string title, DateTimeOffset? played = null, long posMs = 0)
        => new()
        {
            Id = Guid.NewGuid(), FeedId = FeedId, Title = title,
            AudioUrl = "https://ex.test/a.mp3",
            Progress = new EpisodeProgress { LastPlayedAt = played, LastPosMs = posMs },
        };

    private static Episode? Pick(FakeEpisodeStore store, FakeUiShell ui)
        => UiInitialRender.PickLastPlayedEpisode(store, ui);

    [Fact]
    public void The_most_recently_played_episode_wins()
    {
        var store = new FakeEpisodeStore();
        store.Seed(
            Ep("Older", played: DateTimeOffset.UtcNow.AddDays(-3)),
            Ep("Newest", played: DateTimeOffset.UtcNow.AddMinutes(-5)),
            Ep("Middle", played: DateTimeOffset.UtcNow.AddHours(-6)));

        Pick(store, new FakeUiShell())!.Title.Should().Be("Newest");
    }

    [Fact]
    public void Never_played_episodes_lose_to_played_ones()
    {
        var store = new FakeEpisodeStore();
        store.Seed(
            Ep("Never Played"),
            Ep("Played Once", played: DateTimeOffset.UtcNow.AddDays(-30)));

        Pick(store, new FakeUiShell())!.Title.Should().Be("Played Once");
    }

    [Fact]
    public void Progress_breaks_a_tie_on_the_timestamp()
    {
        // Two episodes with no timestamp: prefer the one actually listened to.
        var store = new FakeEpisodeStore();
        store.Seed(
            Ep("Untouched", posMs: 0),
            Ep("Half Done", posMs: 300_000));

        Pick(store, new FakeUiShell())!.Title.Should().Be("Half Done");
    }

    [Fact]
    public void An_empty_library_falls_back_to_the_current_selection()
    {
        var store = new FakeEpisodeStore();
        var ui = new FakeUiShell { SelectedEpisode = Ep("Selected") };

        Pick(store, ui)!.Title.Should().Be("Selected");
    }

    [Fact]
    public void An_empty_library_with_no_selection_picks_nothing()
    {
        Pick(new FakeEpisodeStore(), new FakeUiShell()).Should().BeNull();
    }

    [Fact]
    public void A_single_episode_is_picked_even_if_never_played()
    {
        var store = new FakeEpisodeStore();
        store.Seed(Ep("Only One"));

        Pick(store, new FakeUiShell())!.Title.Should().Be("Only One");
    }

    [Fact]
    public void The_choice_does_not_depend_on_the_order_episodes_were_stored()
    {
        var played = DateTimeOffset.UtcNow.AddMinutes(-1);

        var forward = new FakeEpisodeStore();
        forward.Seed(Ep("A"), Ep("B", played: played), Ep("C"));

        var reversed = new FakeEpisodeStore();
        reversed.Seed(Ep("C"), Ep("B", played: played), Ep("A"));

        Pick(forward, new FakeUiShell())!.Title.Should().Be("B");
        Pick(reversed, new FakeUiShell())!.Title.Should().Be("B");
    }
}
