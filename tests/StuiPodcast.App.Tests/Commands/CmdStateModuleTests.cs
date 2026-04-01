using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdStateModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private Task SaveAsync() => Task.CompletedTask;

    [Fact]
    public void Save_toggle_flips_saved_flag()
    {
        var ep = new Episode { Title = "E", AudioUrl = "x", Saved = false };
        _data.Episodes.Add(ep);
        _ui.SelectedEpisode = ep;

        CmdStateModule.ExecSave(Array.Empty<string>(), _ui, _data, SaveAsync);
        ep.Saved.Should().BeTrue();
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("Saved"));
    }

    [Fact]
    public void Save_on_sets_saved()
    {
        var ep = new Episode { Title = "E", AudioUrl = "x", Saved = false };
        _data.Episodes.Add(ep);
        _ui.SelectedEpisode = ep;

        CmdStateModule.ExecSave(new[] { "on" }, _ui, _data, SaveAsync);
        ep.Saved.Should().BeTrue();
    }

    [Fact]
    public void Save_off_clears_saved()
    {
        var ep = new Episode { Title = "E", AudioUrl = "x", Saved = true };
        _data.Episodes.Add(ep);
        _ui.SelectedEpisode = ep;

        CmdStateModule.ExecSave(new[] { "off" }, _ui, _data, SaveAsync);
        ep.Saved.Should().BeFalse();
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("Unsaved"));
    }

    [Fact]
    public void Save_no_episode_is_noop()
    {
        _ui.SelectedEpisode = null;
        CmdStateModule.ExecSave(Array.Empty<string>(), _ui, _data, SaveAsync);
        _ui.OsdMessages.Should().BeEmpty();
    }
}
