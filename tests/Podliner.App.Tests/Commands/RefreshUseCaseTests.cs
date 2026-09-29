using FluentAssertions;
using Podliner.App.Command;
using Podliner.App.Command.UseCases;
using Podliner.App.Tests.Fakes;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.Commands;

// :refresh fetches now; :refresh auto sets how often it happens on its own
// (#32), the same shape as :sync auto.
public sealed class RefreshUseCaseTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private int _saves;

    private RefreshUseCase Make()
        => new(_ui, _data, () => { _saves++; return Task.CompletedTask; }, () => _now);

    private string LastOsd => _ui.OsdMessages.Last().Text;

    // ── parsing ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(":refresh", new string[0])]
    [InlineData(":refresh auto", new[] { "auto" })]
    [InlineData(":refresh auto 30", new[] { "auto", "30" })]
    [InlineData(":refresh auto off", new[] { "auto", "off" })]
    public void The_command_reaches_refresh_with_its_arguments(string raw, string[] args)
    {
        var parsed = CmdParser.Parse(raw);

        parsed.Kind.Should().Be(TopCommand.Refresh);
        parsed.Args.Should().Equal(args);
    }

    // ── fetching now ─────────────────────────────────────────────────────────

    [Fact]
    public void A_bare_refresh_fetches_now()
    {
        Make().Exec(Array.Empty<string>());

        _ui.RefreshRequests.Should().Be(1);
    }

    // ── the interval ─────────────────────────────────────────────────────────

    [Fact]
    public void An_interval_in_minutes_is_set_and_saved()
    {
        Make().Exec(new[] { "auto", "30" });

        _data.RefreshIntervalMinutes.Should().Be(30);
        _saves.Should().Be(1);
        LastOsd.Should().Be("refresh: every 30m");
        _ui.RefreshRequests.Should().Be(0, "setting the interval is not a request to fetch");
    }

    [Fact]
    public void A_trailing_m_is_understood()
    {
        Make().Exec(new[] { "auto", "45m" });

        _data.RefreshIntervalMinutes.Should().Be(45);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("0")]
    public void Off_switches_it_off(string arg)
    {
        Make().Exec(new[] { "auto", arg });

        _data.RefreshIntervalMinutes.Should().Be(0);
        LastOsd.Should().Be("refresh: automatic refresh off");
    }

    [Fact]
    public void Too_short_an_interval_is_raised_and_says_so()
    {
        Make().Exec(new[] { "auto", "1" });

        _data.RefreshIntervalMinutes.Should().Be(5);
        LastOsd.Should().Be("refresh: every 5m (the shortest allowed)");
    }

    [Fact]
    public void Too_long_an_interval_is_lowered_and_says_so()
    {
        Make().Exec(new[] { "auto", "999999" });

        _data.RefreshIntervalMinutes.Should().Be(10080);
        LastOsd.Should().Contain("the longest allowed");
    }

    [Fact]
    public void Nonsense_changes_nothing_and_shows_the_usage()
    {
        _data.RefreshIntervalMinutes = 60;

        Make().Exec(new[] { "auto", "banana" });

        _data.RefreshIntervalMinutes.Should().Be(60);
        _saves.Should().Be(0);
        LastOsd.Should().StartWith("usage:");
    }

    // ── asking how it stands ─────────────────────────────────────────────────

    [Fact]
    public void Asking_shows_the_interval_and_when_it_last_ran()
    {
        _data.RefreshIntervalMinutes = 60;
        _data.LastRefreshAt = _now.AddMinutes(-12);

        Make().Exec(new[] { "auto" });

        LastOsd.Should().Be("refresh: every 60m, last 12m ago");
    }

    [Fact]
    public void Asking_when_off_says_so()
    {
        _data.RefreshIntervalMinutes = 0;
        _data.LastRefreshAt = null;

        Make().Exec(new[] { "auto" });

        LastOsd.Should().Be("refresh: automatic refresh off, last never");
    }
}
