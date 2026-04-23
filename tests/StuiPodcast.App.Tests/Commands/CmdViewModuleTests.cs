using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdViewModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly FakeFeedStore _feeds = new();
    private readonly ViewUseCase _sut;
    private bool _saved;

    public CmdViewModuleTests()
    {
        Task SaveAsync() { _saved = true; return Task.CompletedTask; }
        _sut = new ViewUseCase(_ui, _data, SaveAsync, _episodes, _feeds);
    }

    [Fact]
    public void Filter_unplayed_sets_flag()
    {
        _data.UnplayedOnly = false;
        _sut.ExecFilter(new[] { "unplayed" });
        _data.UnplayedOnly.Should().BeTrue();
        _ui.LastUnplayedFilterVisual.Should().BeTrue();
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Filter_all_clears_flag()
    {
        _data.UnplayedOnly = true;
        _sut.ExecFilter(new[] { "all" });
        _data.UnplayedOnly.Should().BeFalse();
    }

    [Fact]
    public void Filter_toggle_flips()
    {
        _data.UnplayedOnly = false;
        _sut.ExecFilter(new[] { "toggle" });
        _data.UnplayedOnly.Should().BeTrue();
        _sut.ExecFilter(new[] { "toggle" });
        _data.UnplayedOnly.Should().BeFalse();
    }

    [Fact]
    public void Filter_empty_arg_toggles()
    {
        _data.UnplayedOnly = false;
        _sut.ExecFilter(Array.Empty<string>());
        _data.UnplayedOnly.Should().BeTrue();
    }

    [Fact]
    public void Filter_invalid_shows_usage()
    {
        _sut.ExecFilter(new[] { "bogus" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Sort_reset_sets_defaults()
    {
        _data.SortBy = "title";
        _data.SortDir = "asc";
        _sut.ExecSort(new[] { "reset" });
        _data.SortBy.Should().Be("pubdate");
        _data.SortDir.Should().Be("desc");
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Sort_reverse_toggles_direction()
    {
        _data.SortDir = "desc";
        _sut.ExecSort(new[] { "reverse" });
        _data.SortDir.Should().Be("asc");
        _sut.ExecSort(new[] { "reverse" });
        _data.SortDir.Should().Be("desc");
    }

    [Fact]
    public void Sort_by_title_asc()
    {
        _sut.ExecSort(new[] { "by", "title", "asc" });
        _data.SortBy.Should().Be("title");
        _data.SortDir.Should().Be("asc");
    }

    [Fact]
    public void Sort_by_invalid_key_shows_error()
    {
        _sut.ExecSort(new[] { "by", "bogus" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("invalid"));
    }

    [Fact]
    public void Sort_show_displays_current()
    {
        _data.SortBy = "title";
        _data.SortDir = "asc";
        _sut.ExecSort(new[] { "show" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("title") && m.Text.Contains("asc"));
    }

    [Fact]
    public void PlayerBar_toggle()
    {
        _data.PlayerAtTop = false;
        _sut.ExecPlayerBar(Array.Empty<string>());
        _ui.PlayerPlacementToggled.Should().BeTrue();
        _data.PlayerAtTop.Should().BeTrue();
    }

    [Fact]
    public void PlayerBar_top()
    {
        _sut.ExecPlayerBar(new[] { "top" });
        _ui.LastPlayerPlacement.Should().BeTrue();
        _data.PlayerAtTop.Should().BeTrue();
    }

    [Fact]
    public void PlayerBar_bottom()
    {
        _sut.ExecPlayerBar(new[] { "bottom" });
        _ui.LastPlayerPlacement.Should().BeFalse();
        _data.PlayerAtTop.Should().BeFalse();
    }

    [Fact]
    public void Search_filters_by_title()
    {
        var feedId = Guid.NewGuid();
        _ui.SelectedFeedId = feedId;
        _episodes.Seed(new Episode { Id = Guid.NewGuid(), FeedId = feedId, Title = "Hello World", AudioUrl = "x" });
        _episodes.Seed(new Episode { Id = Guid.NewGuid(), FeedId = feedId, Title = "Goodbye", AudioUrl = "y" });

        _sut.ExecSearch(new[] { "Hello" });

        _ui.SetEpisodeCalls.Should().HaveCount(1);
        _ui.SetEpisodeCalls[0].Episodes.Should().HaveCount(1);
        _ui.SetEpisodeCalls[0].Episodes[0].Title.Should().Be("Hello World");
    }

    [Fact]
    public void Search_clear_resets()
    {
        var feedId = Guid.NewGuid();
        _ui.SelectedFeedId = feedId;
        _episodes.Seed(new Episode { Id = Guid.NewGuid(), FeedId = feedId, Title = "Ep1", AudioUrl = "x" });

        _sut.ExecSearch(new[] { "clear" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("cleared"));
    }
}
