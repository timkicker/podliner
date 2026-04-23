using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdNetModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly FakeFeedStore _feeds = new();
    private readonly NetUseCase _sut;

    public CmdNetModuleTests()
    {
        Task SaveAsync() => Task.CompletedTask;
        var view = new ViewUseCase(_ui, _data, SaveAsync, _episodes, _feeds);
        _sut = new NetUseCase(_ui, _data, SaveAsync, _episodes, view);
    }

    [Fact]
    public void Net_offline_sets_flag()
    {
        _data.NetworkOnline = true;
        _sut.ExecNet(new[] { "offline" });
        _data.NetworkOnline.Should().BeFalse();
        _ui.OsdMessages.Should().Contain(m => m.Text == "Offline");
    }

    [Fact]
    public void Net_online_sets_flag()
    {
        _data.NetworkOnline = false;
        _sut.ExecNet(new[] { "online" });
        _data.NetworkOnline.Should().BeTrue();
        _ui.OsdMessages.Should().Contain(m => m.Text == "Online");
    }

    [Fact]
    public void Net_toggle_flips()
    {
        _data.NetworkOnline = true;
        _sut.ExecNet(new[] { "toggle" });
        _data.NetworkOnline.Should().BeFalse();
    }

    [Fact]
    public void Net_empty_toggles()
    {
        _data.NetworkOnline = true;
        _sut.ExecNet(Array.Empty<string>());
        _data.NetworkOnline.Should().BeFalse();
    }

    [Fact]
    public void Net_invalid_shows_usage()
    {
        _sut.ExecNet(new[] { "banana" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Net_off_alias_works()
    {
        _data.NetworkOnline = true;
        _sut.ExecNet(new[] { "off" });
        _data.NetworkOnline.Should().BeFalse();
    }

    [Fact]
    public void Net_on_alias_works()
    {
        _data.NetworkOnline = false;
        _sut.ExecNet(new[] { "on" });
        _data.NetworkOnline.Should().BeTrue();
    }

    [Fact]
    public void PlaySource_auto()
    {
        _sut.ExecPlaySource(new[] { "auto" });
        _data.PlaySource.Should().Be("auto");
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("auto"));
    }

    [Fact]
    public void PlaySource_local()
    {
        _sut.ExecPlaySource(new[] { "local" });
        _data.PlaySource.Should().Be("local");
    }

    [Fact]
    public void PlaySource_invalid_shows_usage()
    {
        _sut.ExecPlaySource(new[] { "banana" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void PlaySource_show_displays_current()
    {
        _data.PlaySource = "local";
        _sut.ExecPlaySource(new[] { "show" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("local"));
    }

    [Fact]
    public void Offline_updates_window_title_for_playing_episode()
    {
        var ep = new Episode { Id = Guid.NewGuid(), Title = "My Episode", AudioUrl = "x" };
        _episodes.Seed(ep);
        _ui.NowPlayingId = ep.Id;
        _data.NetworkOnline = true;

        _sut.ExecNet(new[] { "offline" });
        _ui.LastWindowTitle.Should().Contain("[OFFLINE]");
        _ui.LastWindowTitle.Should().Contain("My Episode");
    }
}
