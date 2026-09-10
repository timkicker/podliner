using FluentAssertions;
using StuiPodcast.App.Bootstrap;
using Xunit;

namespace StuiPodcast.App.Tests.Bootstrap;

public sealed class CliEntrypointTests
{
    // ── nothing to parse ────────────────────────────────────────────────────

    [Fact]
    public void Null_args_give_all_defaults()
    {
        var o = CliEntrypoint.Parse(null);

        o.ShowVersion.Should().BeFalse();
        o.ShowHelp.Should().BeFalse();
        o.Offline.Should().BeFalse();
        o.Ascii.Should().BeFalse();
        o.NoFileLogs.Should().BeFalse();
        o.Engine.Should().BeNull();
        o.Theme.Should().BeNull();
        o.Feed.Should().BeNull();
        o.Search.Should().BeNull();
        o.OpmlImport.Should().BeNull();
        o.OpmlExport.Should().BeNull();
        o.LogLevel.Should().BeNull();
        o.LogDir.Should().BeNull();
    }

    [Fact]
    public void Empty_args_give_all_defaults()
        => CliEntrypoint.Parse(Array.Empty<string>()).Engine.Should().BeNull();

    [Fact]
    public void Unknown_flags_are_ignored()
    {
        var o = CliEntrypoint.Parse(new[] { "--not-a-flag", "--engine", "mpv" });

        o.Engine.Should().Be("mpv");
    }

    // ── boolean flags ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("-V")]
    public void Version_flag_variants(string flag)
        => CliEntrypoint.Parse(new[] { flag }).ShowVersion.Should().BeTrue();

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void Help_flag_variants(string flag)
        => CliEntrypoint.Parse(new[] { flag }).ShowHelp.Should().BeTrue();

    [Fact]
    public void Offline_ascii_and_no_file_logs_are_switches()
    {
        var o = CliEntrypoint.Parse(new[] { "--offline", "--ascii", "--no-file-logs" });

        o.Offline.Should().BeTrue();
        o.Ascii.Should().BeTrue();
        o.NoFileLogs.Should().BeTrue();
    }

    // ── value flags ─────────────────────────────────────────────────────────

    [Fact]
    public void Engine_is_lowercased_and_trimmed()
        => CliEntrypoint.Parse(new[] { "--engine", "  VLC  " }).Engine.Should().Be("vlc");

    [Fact]
    public void Theme_is_lowercased_and_trimmed()
        => CliEntrypoint.Parse(new[] { "--theme", " Base " }).Theme.Should().Be("base");

    [Fact]
    public void Log_level_is_lowercased()
        => CliEntrypoint.Parse(new[] { "--log-level", "DEBUG" }).LogLevel.Should().Be("debug");

    [Fact]
    public void Feed_keeps_its_casing()
    {
        // Feed takes a GUID or a virtual-feed name; lowercasing a GUID would
        // still parse, but the value is passed through verbatim elsewhere.
        var guid = "A11A0000-0000-0000-0000-00000000A11A";

        CliEntrypoint.Parse(new[] { "--feed", guid }).Feed.Should().Be(guid);
    }

    [Fact]
    public void Search_keeps_casing_and_spaces()
        => CliEntrypoint.Parse(new[] { "--search", "The Vergecast" }).Search.Should().Be("The Vergecast");

    [Fact]
    public void Log_dir_is_trimmed_but_keeps_casing()
        => CliEntrypoint.Parse(new[] { "--log-dir", "  /var/log/Podliner  " })
            .LogDir.Should().Be("/var/log/Podliner");

    [Fact]
    public void Opml_import_and_mode_parse_together()
    {
        var o = CliEntrypoint.Parse(new[] { "--opml-import", "/tmp/subs.opml", "--import-mode", "MERGE" });

        o.OpmlImport.Should().Be("/tmp/subs.opml");
        o.OpmlImportMode.Should().Be("merge");
    }

    [Fact]
    public void Opml_export_parses()
        => CliEntrypoint.Parse(new[] { "--opml-export", "/tmp/out.opml" })
            .OpmlExport.Should().Be("/tmp/out.opml");

    // ── ordering and combinations ───────────────────────────────────────────

    [Fact]
    public void Flags_can_appear_in_any_order()
    {
        var o = CliEntrypoint.Parse(new[]
        {
            "--offline", "--engine", "mpv", "--ascii", "--search", "term", "--theme", "native"
        });

        o.Offline.Should().BeTrue();
        o.Ascii.Should().BeTrue();
        o.Engine.Should().Be("mpv");
        o.Search.Should().Be("term");
        o.Theme.Should().Be("native");
    }

    [Fact]
    public void A_repeated_flag_keeps_the_last_value()
        => CliEntrypoint.Parse(new[] { "--engine", "mpv", "--engine", "ffplay" })
            .Engine.Should().Be("ffplay");

    [Fact]
    public void A_value_flag_consumes_its_argument()
    {
        // "--offline" here is the *value* of --search, not a switch.
        var o = CliEntrypoint.Parse(new[] { "--search", "--offline" });

        o.Search.Should().Be("--offline");
        o.Offline.Should().BeFalse();
    }

    // ── missing values at the end of the line ───────────────────────────────

    [Theory]
    [InlineData("--engine")]
    [InlineData("--theme")]
    [InlineData("--feed")]
    [InlineData("--search")]
    [InlineData("--opml-import")]
    [InlineData("--opml-export")]
    [InlineData("--log-level")]
    [InlineData("--log-dir")]
    public void A_trailing_value_flag_without_a_value_does_not_throw(string flag)
    {
        var act = () => CliEntrypoint.Parse(new[] { flag });

        act.Should().NotThrow();
    }

    [Fact]
    public void A_trailing_value_flag_leaves_the_option_unset()
    {
        // Nothing follows --engine, so it stays null and the app keeps the
        // configured engine rather than being reset.
        CliEntrypoint.Parse(new[] { "--offline", "--engine" }).Engine.Should().BeNull();
    }
}
