using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdStateModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly FakeFeedStore _feeds = new();
    private readonly StateUseCase _sut;

    public CmdStateModuleTests()
    {
        Task SaveAsync() => Task.CompletedTask;
        var view = new ViewUseCase(_ui, _data, SaveAsync, _episodes, _feeds);
        _sut = new StateUseCase(_ui, SaveAsync, _episodes, view);
    }

    [Fact]
    public void Save_toggle_flips_saved_flag()
    {
        var ep = new Episode { Id = Guid.NewGuid(), Title = "E", AudioUrl = "x", Saved = false };
        _episodes.Seed(ep);
        _ui.SelectedEpisode = ep;

        _sut.ExecSave(Array.Empty<string>());
        ep.Saved.Should().BeTrue();
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("Saved"));
    }

    [Fact]
    public void Save_on_sets_saved()
    {
        var ep = new Episode { Id = Guid.NewGuid(), Title = "E", AudioUrl = "x", Saved = false };
        _episodes.Seed(ep);
        _ui.SelectedEpisode = ep;

        _sut.ExecSave(new[] { "on" });
        ep.Saved.Should().BeTrue();
    }

    [Fact]
    public void Save_off_clears_saved()
    {
        var ep = new Episode { Id = Guid.NewGuid(), Title = "E", AudioUrl = "x", Saved = true };
        _episodes.Seed(ep);
        _ui.SelectedEpisode = ep;

        _sut.ExecSave(new[] { "off" });
        ep.Saved.Should().BeFalse();
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("Unsaved"));
    }

    [Fact]
    public void Save_no_episode_is_noop()
    {
        _ui.SelectedEpisode = null;
        _sut.ExecSave(Array.Empty<string>());
        _ui.OsdMessages.Should().BeEmpty();
    }
}
