using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class EpisodeListBuilderTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();

    [Fact]
    public void No_feed_selected_returns_empty()
    {
        _ui.SelectedFeedId = null;
        EpisodeListBuilder.BuildCurrentList(_ui, _data).Should().BeEmpty();
    }

    [Fact]
    public void Virtual_All_returns_all_episodes()
    {
        var feedA = Guid.NewGuid();
        var feedB = Guid.NewGuid();
        _data.Episodes.Add(new Episode { FeedId = feedA, Title = "A", AudioUrl = "a" });
        _data.Episodes.Add(new Episode { FeedId = feedB, Title = "B", AudioUrl = "b" });

        _ui.SelectedFeedId = VirtualFeedsCatalog.All;

        EpisodeListBuilder.BuildCurrentList(_ui, _data).Should().HaveCount(2);
    }

    [Fact]
    public void Specific_feed_filters_to_its_episodes()
    {
        var feedA = Guid.NewGuid();
        var feedB = Guid.NewGuid();
        _data.Episodes.Add(new Episode { FeedId = feedA, Title = "A", AudioUrl = "a" });
        _data.Episodes.Add(new Episode { FeedId = feedB, Title = "B", AudioUrl = "b" });
        _data.Episodes.Add(new Episode { FeedId = feedA, Title = "A2", AudioUrl = "a2" });

        _ui.SelectedFeedId = feedA;

        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data);
        list.Should().HaveCount(2);
        list.Should().OnlyContain(e => e.FeedId == feedA);
    }

    [Fact]
    public void Virtual_Saved_returns_only_saved_episodes()
    {
        var fid = Guid.NewGuid();
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "A", AudioUrl = "a", Saved = true });
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "B", AudioUrl = "b", Saved = false });
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "C", AudioUrl = "c", Saved = true });

        _ui.SelectedFeedId = VirtualFeedsCatalog.Saved;

        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data);
        list.Should().HaveCount(2);
        list.Should().OnlyContain(e => e.Saved);
    }

    [Fact]
    public void Virtual_History_filters_and_sorts_by_last_played()
    {
        var fid = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-5);

        _data.Episodes.Add(new Episode
        {
            FeedId = fid, Title = "Never", AudioUrl = "n",
            Progress = new EpisodeProgress() // LastPlayedAt = null
        });
        _data.Episodes.Add(new Episode
        {
            FeedId = fid, Title = "Older", AudioUrl = "o",
            Progress = new EpisodeProgress { LastPlayedAt = older }
        });
        _data.Episodes.Add(new Episode
        {
            FeedId = fid, Title = "Newer", AudioUrl = "new",
            Progress = new EpisodeProgress { LastPlayedAt = newer }
        });

        _ui.SelectedFeedId = VirtualFeedsCatalog.History;

        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data);
        list.Should().HaveCount(2);
        list[0].Title.Should().Be("Newer", "history sorts by LastPlayedAt desc");
        list[1].Title.Should().Be("Older");
    }

    [Fact]
    public void UnplayedOnly_filters_out_manually_marked_played()
    {
        var fid = Guid.NewGuid();
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "U", AudioUrl = "u", ManuallyMarkedPlayed = false });
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "P", AudioUrl = "p", ManuallyMarkedPlayed = true });
        _data.UnplayedOnly = true;
        _ui.SelectedFeedId = fid;

        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data);
        list.Should().HaveCount(1);
        list[0].Title.Should().Be("U");
    }

    [Fact]
    public void Default_sort_is_pubdate_desc()
    {
        var fid = Guid.NewGuid();
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "old", AudioUrl = "o", PubDate = DateTimeOffset.UtcNow.AddDays(-2) });
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "new", AudioUrl = "n", PubDate = DateTimeOffset.UtcNow.AddDays(-1) });
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "nul", AudioUrl = "x" }); // null PubDate

        _ui.SelectedFeedId = fid;
        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data);

        list[0].Title.Should().Be("new");
        list[1].Title.Should().Be("old");
        list[2].Title.Should().Be("nul");
    }

    [Fact]
    public void Empty_episodes_returns_empty()
    {
        _ui.SelectedFeedId = VirtualFeedsCatalog.All;
        EpisodeListBuilder.BuildCurrentList(_ui, _data).Should().BeEmpty();
    }

    [Fact]
    public void UnplayedOnly_applies_to_Saved_virtual_feed_too()
    {
        var fid = Guid.NewGuid();
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "A", AudioUrl = "a", Saved = true, ManuallyMarkedPlayed = false });
        _data.Episodes.Add(new Episode { FeedId = fid, Title = "B", AudioUrl = "b", Saved = true, ManuallyMarkedPlayed = true });
        _data.UnplayedOnly = true;
        _ui.SelectedFeedId = VirtualFeedsCatalog.Saved;

        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data);
        list.Should().HaveCount(1);
        list[0].Title.Should().Be("A");
    }
}
