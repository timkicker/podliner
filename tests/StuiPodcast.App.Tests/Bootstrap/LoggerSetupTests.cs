using FluentAssertions;
using Serilog.Events;
using StuiPodcast.App.Bootstrap;
using Xunit;

namespace StuiPodcast.App.Tests.Bootstrap;

public sealed class LoggerLevelTests
{
    [Theory]
    [InlineData("info", LogEventLevel.Information)]
    [InlineData("warn", LogEventLevel.Warning)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    [InlineData("debug", LogEventLevel.Debug)]
    public void Known_levels_map_through(string input, LogEventLevel expected)
        => LoggerSetup.ParseLevel(input).Should().Be(expected);

    [Theory]
    [InlineData("INFO")]
    [InlineData("  info  ")]
    [InlineData("Info")]
    public void The_level_is_case_and_whitespace_insensitive(string input)
        => LoggerSetup.ParseLevel(input).Should().Be(LogEventLevel.Information);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    public void Anything_else_falls_back_to_debug(string? input)
        => LoggerSetup.ParseLevel(input).Should().Be(LogEventLevel.Debug);
}

// Where the log file lands. Issue #19 was podliner trying to write logs next
// to the binary, which on a system-wide install means /usr/bin/logs and a
// crash on launch. These pin the resolution order so it cannot drift back.
[Collection("env")]
public sealed class LoggerLogDirTests : IDisposable
{
    private readonly string? _origOverride = Environment.GetEnvironmentVariable("PODLINER_LOG_DIR");
    private readonly string? _origState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

    public LoggerLogDirTests()
    {
        Environment.SetEnvironmentVariable("PODLINER_LOG_DIR", null);
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PODLINER_LOG_DIR", _origOverride);
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", _origState);
    }

    // ── precedence ──────────────────────────────────────────────────────────

    [Fact]
    public void The_cli_flag_wins_over_everything()
    {
        Environment.SetEnvironmentVariable("PODLINER_LOG_DIR", "/from/env");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", "/from/xdg");

        LoggerSetup.ResolveLogDir("/from/cli").Should().Be("/from/cli");
    }

    [Fact]
    public void The_env_override_wins_over_the_platform_default()
    {
        Environment.SetEnvironmentVariable("PODLINER_LOG_DIR", "/from/env");

        LoggerSetup.ResolveLogDir(null).Should().Be("/from/env");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_cli_flag_is_ignored(string? cli)
    {
        Environment.SetEnvironmentVariable("PODLINER_LOG_DIR", "/from/env");

        LoggerSetup.ResolveLogDir(cli).Should().Be("/from/env");
    }

    [Fact]
    public void A_blank_env_override_is_ignored()
    {
        Environment.SetEnvironmentVariable("PODLINER_LOG_DIR", "   ");

        LoggerSetup.ResolveLogDir(null).Should().NotBe("   ");
    }

    // ── the platform default ────────────────────────────────────────────────

    [Fact]
    public void On_unix_XDG_STATE_HOME_is_honoured()
    {
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", "/tmp/xdg-state");

        LoggerSetup.ResolveLogDir(null)
            .Should().Be(Path.Combine("/tmp/xdg-state", "podliner", "logs"));
    }

    [Fact]
    public void On_unix_without_XDG_it_lands_under_the_home_directory()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = LoggerSetup.ResolveLogDir(null);

        dir.Should().NotBeNull();
        dir!.Should().Contain(".local");
        dir.Should().EndWith(Path.Combine("podliner", "logs"));
    }

    [Fact]
    public void On_windows_it_lands_under_local_appdata()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = LoggerSetup.ResolveLogDir(null);

        dir.Should().NotBeNull();
        dir!.Should().EndWith(Path.Combine("podliner", "logs"));
    }

    // ── the regression from issue #19 ───────────────────────────────────────

    [Fact]
    public void The_default_is_never_next_to_the_binary()
    {
        // A system-wide install puts the binary in /usr/bin, so a relative
        // "logs" directory means /usr/bin/logs and a crash on launch.
        var dir = LoggerSetup.ResolveLogDir(null);
        dir.Should().NotBeNull();

        var resolved = dir!;
        Path.IsPathRooted(resolved).Should().BeTrue();
        resolved.Should().NotBe("logs");
        resolved.Should().NotStartWith("/usr/bin");
        Path.GetFullPath(resolved).Should().NotBe(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs")));
    }

    [Fact]
    public void The_default_always_ends_in_a_podliner_logs_folder()
        => LoggerSetup.ResolveLogDir(null)!
            .Should().EndWith(Path.Combine("podliner", "logs"));

    [Fact]
    public void Resolution_is_stable_across_calls()
        => LoggerSetup.ResolveLogDir(null).Should().Be(LoggerSetup.ResolveLogDir(null));
}
