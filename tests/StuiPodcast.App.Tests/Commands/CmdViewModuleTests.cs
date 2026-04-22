using FluentAssertions;
using StuiPodcast.App.Command.Module;
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
    private bool _saved;
    private Task SaveAsync() { _saved = true; return Task.CompletedTask; }

    [Fact]
    public void Filter_unplayed_sets_flag()
    {
        _data.UnplayedOnly = false;
        CmdViewModule.ExecFilter(new[] { "unplayed" }, _ui, _data, SaveAsync);
        _data.UnplayedOnly.Should().BeTrue();
        _ui.LastUnplayedFilterVisual.Should().BeTrue();
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Filter_all_clears_flag()
    {
        _data.UnplayedOnly = true;
        CmdViewModule.ExecFilter(new[] { "all" }, _ui, _data, SaveAsync);
        _data.UnplayedOnly.Should().BeFalse();
    }

    [Fact]
    public void Filter_toggle_flips()
    {
        _data.UnplayedOnly = false;
        CmdViewModule.ExecFilter(new[] { "toggle" }, _ui, _data, SaveAsync);
        _data.UnplayedOnly.Should().BeTrue();
        CmdViewModule.ExecFilter(new[] { "toggle" }, _ui, _data, SaveAsync);
        _data.UnplayedOnly.Should().BeFalse();
    }

    [Fact]
    public void Filter_empty_arg_toggles()
    {
        _data.UnplayedOnly = false;
        CmdViewModule.ExecFilter(Array.Empty<string>(), _ui, _data, SaveAsync);
        _data.UnplayedOnly.Should().BeTrue();
    }

    [Fact]
    public void Filter_invalid_shows_usage()
    {
        CmdViewModule.ExecFilter(new[] { "bogus" }, _ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Sort_reset_sets_defaults()
    {
        _data.SortBy = "title";
        _data.SortDir = "asc";
        CmdViewModule.ExecSort(new[] { "reset" }, _ui, _data, SaveAsync, _feeds, _episodes);
        _data.SortBy.Should().Be("pubdate");
        _data.SortDir.Should().Be("desc");
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Sort_reverse_toggles_direction()
    {
        _data.SortDir = "desc";
        CmdViewModule.ExecSort(new[] { "reverse" }, _ui, _data, SaveAsync, _feeds, _episodes);
        _data.SortDir.Should().Be("asc");
        CmdViewModule.ExecSort(new[] { "reverse" }, _ui, _data, SaveAsync, _feeds, _episodes);
        _data.SortDir.Should().Be("desc");
    }

    [Fact]
    public void Sort_by_title_asc()
    {
        CmdViewModule.ExecSort(new[] { "by", "title", "asc" }, _ui, _data, SaveAsync, _feeds, _episodes);
        _data.SortBy.Should().Be("title");
        _data.SortDir.Should().Be("asc");
    }

    [Fact]
    public void Sort_by_invalid_key_shows_error()
    {
        CmdViewModule.ExecSort(new[] { "by", "bogus" }, _ui, _data, SaveAsync, _feeds, _episodes);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("invalid"));
    }

    [Fact]
    public void Sort_show_displays_current()
    {
        _data.SortBy = "title";
        _data.SortDir = "asc";
        CmdViewModule.ExecSort(new[] { "show" }, _ui, _data, SaveAsync, _feeds, _episodes);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("title") && m.Text.Contains("asc"));
    }

    [Fact]
    public void PlayerBar_toggle()
    {
        _data.PlayerAtTop = false;
        CmdViewModule.ExecPlayerBar(Array.Empty<string>(), _ui, _data, SaveAsync);
        _ui.PlayerPlacementToggled.Should().BeTrue();
        _data.PlayerAtTop.Should().BeTrue();
    }

    [Fact]
    public void PlayerBar_top()
    {
        CmdViewModule.ExecPlayerBar(new[] { "top" }, _ui, _data, SaveAsync);
        _ui.LastPlayerPlacement.Should().BeTrue();
        _data.PlayerAtTop.Should().BeTrue();
    }

    [Fact]
    public void PlayerBar_bottom()
    {
        CmdViewModule.ExecPlayerBar(new[] { "bottom" }, _ui, _data, SaveAsync);
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

        CmdViewModule.ExecSearch(new[] { "Hello" }, _ui, _data, _episodes);

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

        CmdViewModule.ExecSearch(new[] { "clear" }, _ui, _data, _episodes);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("cleared"));
    }
}
