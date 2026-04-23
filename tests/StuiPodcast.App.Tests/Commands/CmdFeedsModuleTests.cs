using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdFeedsModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly FeedUseCase _sut;

    public CmdFeedsModuleTests()
    {
        Task SaveAsync() => Task.CompletedTask;
        _sut = new FeedUseCase(_ui, _data, SaveAsync, _episodes);
    }

    [Fact]
    public void AddFeed_with_url_requests_add()
    {
        _sut.ExecAddFeed(new[] { "https://example.com/feed.xml" });
        _ui.LastRequestedAddFeedUrl.Should().Be("https://example.com/feed.xml");
    }

    [Fact]
    public void AddFeed_empty_shows_usage()
    {
        _sut.ExecAddFeed(Array.Empty<string>());
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Feed_all_selects_virtual_all()
    {
        _sut.ExecFeed(new[] { "all" });
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.All);
    }

    [Fact]
    public void Feed_saved_selects_virtual_saved()
    {
        _sut.ExecFeed(new[] { "saved" });
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.Saved);
    }

    [Fact]
    public void Feed_downloaded_selects_virtual_downloaded()
    {
        _sut.ExecFeed(new[] { "downloaded" });
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.Downloaded);
    }

    [Fact]
    public void Feed_history_selects_virtual_history()
    {
        _sut.ExecFeed(new[] { "history" });
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.History);
    }

    [Fact]
    public void Feed_queue_selects_virtual_queue()
    {
        _sut.ExecFeed(new[] { "queue" });
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.Queue);
    }

    [Fact]
    public void Feed_unknown_shows_usage()
    {
        _sut.ExecFeed(new[] { "banana" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void RemoveSelectedFeed_no_selection_shows_error()
    {
        _ui.SelectedFeedId = null;
        _sut.RemoveSelectedFeed();
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("No feed selected"));
    }

    [Fact]
    public void RemoveSelectedFeed_virtual_feed_shows_error()
    {
        _ui.SelectedFeedId = VirtualFeedsCatalog.All;
        _sut.RemoveSelectedFeed();
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("Can't remove virtual"));
    }

    [Fact]
    public void RemoveSelectedFeed_real_feed_requests_remove()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        _sut.RemoveSelectedFeed();
        _ui.RemoveFeedRequested.Should().BeTrue();
    }
}
