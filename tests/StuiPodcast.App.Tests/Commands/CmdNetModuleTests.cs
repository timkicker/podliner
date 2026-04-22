using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdNetModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeEpisodeStore _episodes = new();
    private Task SaveAsync() => Task.CompletedTask;

    [Fact]
    public void Net_offline_sets_flag()
    {
        _data.NetworkOnline = true;
        CmdNetModule.ExecNet(new[] { "offline" }, _ui, _data, SaveAsync, _episodes);
        _data.NetworkOnline.Should().BeFalse();
        _ui.OsdMessages.Should().Contain(m => m.Text == "Offline");
    }

    [Fact]
    public void Net_online_sets_flag()
    {
        _data.NetworkOnline = false;
        CmdNetModule.ExecNet(new[] { "online" }, _ui, _data, SaveAsync, _episodes);
        _data.NetworkOnline.Should().BeTrue();
        _ui.OsdMessages.Should().Contain(m => m.Text == "Online");
    }

    [Fact]
    public void Net_toggle_flips()
    {
        _data.NetworkOnline = true;
        CmdNetModule.ExecNet(new[] { "toggle" }, _ui, _data, SaveAsync, _episodes);
        _data.NetworkOnline.Should().BeFalse();
    }

    [Fact]
    public void Net_empty_toggles()
    {
        _data.NetworkOnline = true;
        CmdNetModule.ExecNet(Array.Empty<string>(), _ui, _data, SaveAsync, _episodes);
        _data.NetworkOnline.Should().BeFalse();
    }

    [Fact]
    public void Net_invalid_shows_usage()
    {
        CmdNetModule.ExecNet(new[] { "banana" }, _ui, _data, SaveAsync, _episodes);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Net_off_alias_works()
    {
        _data.NetworkOnline = true;
        CmdNetModule.ExecNet(new[] { "off" }, _ui, _data, SaveAsync, _episodes);
        _data.NetworkOnline.Should().BeFalse();
    }

    [Fact]
    public void Net_on_alias_works()
    {
        _data.NetworkOnline = false;
        CmdNetModule.ExecNet(new[] { "on" }, _ui, _data, SaveAsync, _episodes);
        _data.NetworkOnline.Should().BeTrue();
    }

    [Fact]
    public void PlaySource_auto()
    {
        CmdNetModule.ExecPlaySource(new[] { "auto" }, _ui, _data, SaveAsync);
        _data.PlaySource.Should().Be("auto");
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("auto"));
    }

    [Fact]
    public void PlaySource_local()
    {
        CmdNetModule.ExecPlaySource(new[] { "local" }, _ui, _data, SaveAsync);
        _data.PlaySource.Should().Be("local");
    }

    [Fact]
    public void PlaySource_invalid_shows_usage()
    {
        CmdNetModule.ExecPlaySource(new[] { "banana" }, _ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void PlaySource_show_displays_current()
    {
        _data.PlaySource = "local";
        CmdNetModule.ExecPlaySource(new[] { "show" }, _ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("local"));
    }

    [Fact]
    public void Offline_updates_window_title_for_playing_episode()
    {
        var ep = new Episode { Id = Guid.NewGuid(), Title = "My Episode", AudioUrl = "x" };
        _episodes.Seed(ep);
        _ui.NowPlayingId = ep.Id;
        _data.NetworkOnline = true;

        CmdNetModule.ExecNet(new[] { "offline" }, _ui, _data, SaveAsync, _episodes);
        _ui.LastWindowTitle.Should().Contain("[OFFLINE]");
        _ui.LastWindowTitle.Should().Contain("My Episode");
    }
}
