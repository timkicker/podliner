using FluentAssertions;
using StuiPodcast.App.Command;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

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
}
