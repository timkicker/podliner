using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdFeedsModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private Task SaveAsync() => Task.CompletedTask;

    [Fact]
    public void AddFeed_with_url_requests_add()
    {
        CmdFeedsModule.ExecAddFeed(new[] { "https://example.com/feed.xml" }, _ui);
        _ui.LastRequestedAddFeedUrl.Should().Be("https://example.com/feed.xml");
    }

    [Fact]
    public void AddFeed_empty_shows_usage()
    {
        CmdFeedsModule.ExecAddFeed(Array.Empty<string>(), _ui);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Feed_all_selects_virtual_all()
    {
        CmdFeedsModule.ExecFeed(new[] { "all" }, _ui, _data, SaveAsync);
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.All);
    }

    [Fact]
    public void Feed_saved_selects_virtual_saved()
    {
        CmdFeedsModule.ExecFeed(new[] { "saved" }, _ui, _data, SaveAsync);
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.Saved);
    }

    [Fact]
    public void Feed_downloaded_selects_virtual_downloaded()
    {
        CmdFeedsModule.ExecFeed(new[] { "downloaded" }, _ui, _data, SaveAsync);
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.Downloaded);
    }

    [Fact]
    public void Feed_history_selects_virtual_history()
    {
        CmdFeedsModule.ExecFeed(new[] { "history" }, _ui, _data, SaveAsync);
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.History);
    }

    [Fact]
    public void Feed_queue_selects_virtual_queue()
    {
        CmdFeedsModule.ExecFeed(new[] { "queue" }, _ui, _data, SaveAsync);
        _ui.SelectedFeedId.Should().Be(VirtualFeedsCatalog.Queue);
    }

    [Fact]
    public void Feed_unknown_shows_usage()
    {
        CmdFeedsModule.ExecFeed(new[] { "banana" }, _ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void RemoveSelectedFeed_no_selection_shows_error()
    {
        _ui.SelectedFeedId = null;
        CmdFeedsModule.RemoveSelectedFeed(_ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("No feed selected"));
    }

    [Fact]
    public void RemoveSelectedFeed_virtual_feed_shows_error()
    {
        _ui.SelectedFeedId = VirtualFeedsCatalog.All;
        CmdFeedsModule.RemoveSelectedFeed(_ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("Can't remove virtual"));
    }

    [Fact]
    public void RemoveSelectedFeed_real_feed_requests_remove()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        CmdFeedsModule.RemoveSelectedFeed(_ui, _data, SaveAsync);
        _ui.RemoveFeedRequested.Should().BeTrue();
    }
}
