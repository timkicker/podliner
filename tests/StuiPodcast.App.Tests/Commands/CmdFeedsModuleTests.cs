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

    // ── Per-feed settings: :feed speed, :feed auto-download ─────────────────

    private (FeedUseCase sut, FakeFeedStore feeds, Feed feed) MakeWithFeedStore()
    {
        var store = new FakeFeedStore();
        var feed = new Feed { Id = Guid.NewGuid(), Title = "Example", Url = "https://ex/rss" };
        store.Seed(feed);
        _ui.SelectedFeedId = feed.Id;
        Task SaveAsync() => Task.CompletedTask;
        var sut = new FeedUseCase(_ui, _data, SaveAsync, _episodes, store);
        return (sut, store, feed);
    }

    [Fact]
    public void Feed_speed_with_no_args_shows_status_inherit()
    {
        var (sut, _, _) = MakeWithFeedStore();
        sut.ExecFeed(new[] { "speed" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("inheriting"));
    }

    [Fact]
    public void Feed_speed_sets_override_and_clamps()
    {
        var (sut, store, feed) = MakeWithFeedStore();
        sut.ExecFeed(new[] { "speed", "1.5" });
        store.Find(feed.Id)!.SpeedOverride.Should().Be(1.5);

        sut.ExecFeed(new[] { "speed", "99" });
        store.Find(feed.Id)!.SpeedOverride.Should().Be(3.0, "values are clamped to [0.25, 3.0]");
    }

    [Theory]
    [InlineData("off")]
    [InlineData("clear")]
    [InlineData("none")]
    public void Feed_speed_off_clears_override(string off)
    {
        var (sut, store, feed) = MakeWithFeedStore();
        sut.ExecFeed(new[] { "speed", "1.25" });
        store.Find(feed.Id)!.SpeedOverride.Should().NotBeNull();

        sut.ExecFeed(new[] { "speed", off });
        store.Find(feed.Id)!.SpeedOverride.Should().BeNull();
    }

    [Fact]
    public void Feed_speed_bad_value_shows_usage()
    {
        var (sut, _, _) = MakeWithFeedStore();
        sut.ExecFeed(new[] { "speed", "banana" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Feed_speed_on_virtual_feed_shows_error()
    {
        var (sut, _, _) = MakeWithFeedStore();
        _ui.SelectedFeedId = VirtualFeedsCatalog.All;
        sut.ExecFeed(new[] { "speed", "1.5" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("virtual"));
    }

    [Fact]
    public void Feed_auto_download_toggle_flips_flag()
    {
        var (sut, store, feed) = MakeWithFeedStore();
        sut.ExecFeed(new[] { "auto-download", "on" });
        store.Find(feed.Id)!.AutoDownload.Should().BeTrue();

        sut.ExecFeed(new[] { "auto-download", "off" });
        store.Find(feed.Id)!.AutoDownload.Should().BeFalse();

        sut.ExecFeed(new[] { "auto-download" }); // toggle
        store.Find(feed.Id)!.AutoDownload.Should().BeTrue();
    }
}
