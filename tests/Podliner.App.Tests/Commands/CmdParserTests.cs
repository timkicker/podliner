using FluentAssertions;
using Podliner.App.Command;
using Xunit;

namespace Podliner.App.Tests.Commands;

public sealed class CmdParserTests
{
    [Fact]
    public void Empty_input_returns_unknown()
    {
        var parsed = CmdParser.Parse("   ");
        parsed.Cmd.Should().Be("");
        parsed.Args.Should().BeEmpty();
        parsed.Kind.Should().Be(TopCommand.Unknown);
    }

    [Fact]
    public void Adds_colon_and_resolves_aliases()
    {
        CmdParser.Parse("h").Cmd.Should().Be(":help");
        CmdParser.Parse("h").Kind.Should().Be(TopCommand.Help);

        CmdParser.Parse("q").Cmd.Should().Be(":quit");
        CmdParser.Parse("q").Kind.Should().Be(TopCommand.Quit);

        CmdParser.Parse(":r").Cmd.Should().Be(":refresh");
        CmdParser.Parse(":r").Kind.Should().Be(TopCommand.Refresh);
    }

    [Fact]
    public void Tokenizes_quotes_and_escapes()
    {
        var parsed = CmdParser.Parse(":search \"hello world\"");
        parsed.Cmd.Should().Be(":search");
        parsed.Kind.Should().Be(TopCommand.Search);
        parsed.Args.Should().Equal("hello world");

        var parsed2 = CmdParser.Parse(":osd \"a \\\"quote\\\" b\"");
        parsed2.Cmd.Should().Be(":osd");
        parsed2.Kind.Should().Be(TopCommand.Osd);
        parsed2.Args.Should().Equal("a \"quote\" b");
    }

    [Fact]
    public void Unclosed_quote_is_tolerated()
    {
        var parsed = CmdParser.Parse(":search \"abc");
        parsed.Cmd.Should().Be(":search");
        parsed.Kind.Should().Be(TopCommand.Search);
        parsed.Args.Should().Equal("abc");
    }

    [Fact]
    public void Prefix_commands_map_to_top_command()
    {
        var parsed = CmdParser.Parse(":opml import file.opml");
        parsed.Cmd.Should().Be(":opml");
        parsed.Kind.Should().Be(TopCommand.Opml);
        parsed.Args.Should().Equal("import", "file.opml");

        var parsed2 = CmdParser.Parse("theme user");
        parsed2.Cmd.Should().Be(":theme");
        parsed2.Kind.Should().Be(TopCommand.Theme);
        parsed2.Args.Should().Equal("user");

        var parsed3 = CmdParser.Parse(":update");
        parsed3.Kind.Should().Be(TopCommand.Refresh);
    }
    [Theory]
    [InlineData(":redraw")]
    [InlineData(":REDRAW")]
    public void Redraw_parses(string raw)
        => CmdParser.Parse(raw).Kind.Should().Be(TopCommand.Redraw);

    // ── windows paths ───────────────────────────────────────────────────────
    //
    // The tokenizer treated every backslash as an escape and dropped it, so
    // `:opml import C:\Users\tim\feeds.opml` arrived as
    // `C:Userstimfeeds.opml` and the file was never found. Every command that
    // takes a path was broken on Windows, including the `--opml-import` and
    // `--opml-export` CLI flags and `:downloads set-dir`.

    [Fact]
    public void A_windows_path_keeps_its_backslashes()
    {
        var parsed = CmdParser.Parse(@":opml import C:\Users\tim\feeds.opml");

        parsed.Kind.Should().Be(TopCommand.Opml);
        parsed.Args.Should().Equal("import", @"C:\Users\tim\feeds.opml");
    }

    [Fact]
    public void A_quoted_windows_path_with_spaces_survives_too()
    {
        var parsed = CmdParser.Parse(":downloads set-dir \"C:\\Users\\tim\\My Podcasts\"");

        parsed.Args.Should().Equal("set-dir", @"C:\Users\tim\My Podcasts");
    }

    [Fact]
    public void A_unc_path_keeps_both_leading_slashes()
    {
        var parsed = CmdParser.Parse(@":opml export \\nas\share\feeds.opml");

        parsed.Args.Should().Equal("export", @"\\nas\share\feeds.opml");
    }

    [Fact]
    public void An_escaped_quote_still_works()
    {
        var parsed = CmdParser.Parse(":osd \"a \\\"quote\\\" b\"");

        parsed.Args.Should().Equal("a \"quote\" b");
    }

    [Fact]
    public void An_escaped_space_still_joins_one_token()
    {
        var parsed = CmdParser.Parse(@":search two\ words");

        parsed.Args.Should().Equal("two words");
    }
}
