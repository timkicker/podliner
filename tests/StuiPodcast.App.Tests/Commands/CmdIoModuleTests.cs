using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

// Tests focus on the selectable logic of :open and :copy: correct text is
// chosen, correct error messages fire for bad inputs. The actual clipboard /
// system-open work is platform-specific and untestable in CI; we verify those
// paths indirectly via the OSD fallback.
public sealed class CmdIoModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly FakeFeedStore _feeds = new();
    private readonly IoUseCase _sut;

    public CmdIoModuleTests()
    {
        _sut = new IoUseCase(_ui, _feeds);
    }

    [Fact]
    public void ExecCopy_no_episode_shows_error()
    {
        _ui.SelectedEpisode = null;
        _sut.ExecCopy(new[] { "url" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("no episode selected"));
    }

    [Fact]
    public void ExecCopy_default_uses_audio_url()
    {
        var ep = new Episode { Title = "t", AudioUrl = "https://x.com/e.mp3" };
        _ui.SelectedEpisode = ep;

        _sut.ExecCopy(Array.Empty<string>());

        _ui.OsdMessages.Should().Contain(m =>
            m.Text == "copied" || m.Text.Contains("https://x.com/e.mp3"));
    }

    [Fact]
    public void ExecCopy_title_uses_episode_title()
    {
        var ep = new Episode { Title = "My Title", AudioUrl = "https://x.com/e.mp3" };
        _ui.SelectedEpisode = ep;

        _sut.ExecCopy(new[] { "title" });

        _ui.OsdMessages.Should().Contain(m =>
            m.Text == "copied" || m.Text.Contains("My Title"));
    }

    [Fact]
    public void ExecCopy_empty_title_reports_nothing_to_copy()
    {
        var ep = new Episode { Title = "", AudioUrl = "https://x.com/e.mp3" };
        _ui.SelectedEpisode = ep;

        _sut.ExecCopy(new[] { "title" });

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("nothing to copy"));
    }

    [Fact]
    public void ExecCopy_guid_falls_back_to_episode_id()
    {
        var ep = new Episode { Title = "t", AudioUrl = "https://x.com/e.mp3" };
        _ui.SelectedEpisode = ep;

        _sut.ExecCopy(new[] { "guid" });

        _ui.OsdMessages.Should().Contain(m =>
            m.Text == "copied" || m.Text.Contains(ep.Id.ToString()));
    }

    [Fact]
    public void ExecCopy_empty_audio_url_reports_nothing_to_copy()
    {
        var ep = new Episode { Title = "t", AudioUrl = "" };
        _ui.SelectedEpisode = ep;

        _sut.ExecCopy(new[] { "url" });

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("nothing to copy"));
    }

    [Fact]
    public void ExecOpen_no_episode_shows_error()
    {
        _ui.SelectedEpisode = null;
        _sut.ExecOpen(new[] { "audio" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("no episode selected"));
    }

    [Fact]
    public void ExecOpen_audio_with_empty_url_reports_no_url()
    {
        var ep = new Episode { Title = "t", AudioUrl = "" };
        _ui.SelectedEpisode = ep;

        _sut.ExecOpen(new[] { "audio" });

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("no URL"));
    }
}
