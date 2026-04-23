using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdSleepUseCaseTests
{
    readonly FakeUiShell _ui = new();
    readonly SleepTimer _timer = new(onFire: () => { });

    SleepUseCase Make() => new(_ui, _timer);

    // ── empty arg → status ───────────────────────────────────────────────────

    [Fact]
    public void Empty_arg_with_no_timer_shows_usage()
    {
        Make().Exec(Array.Empty<string>());
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("no timer set"));
    }

    [Fact]
    public void Status_with_no_timer_shows_usage()
    {
        Make().Exec(new[] { "status" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("no timer set"));
    }

    // ── off/cancel ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("off")]
    [InlineData("cancel")]
    [InlineData("stop")]
    public void Off_variants_with_no_timer_show_hint(string off)
    {
        Make().Exec(new[] { off });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("no timer set"));
    }

    // ── valid duration ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("30m")]
    [InlineData("1h30m")]
    [InlineData("45s")]
    public void Valid_duration_confirms_set(string dur)
    {
        Make().Exec(new[] { dur });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("timer set for"));
    }

    // ── invalid input ────────────────────────────────────────────────────────

    [Fact]
    public void Invalid_duration_shows_hint()
    {
        Make().Exec(new[] { "banana" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("bad duration"));
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("30m"));
    }
}
