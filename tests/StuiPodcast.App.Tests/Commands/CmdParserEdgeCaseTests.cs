using FluentAssertions;
using StuiPodcast.App.Command;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdParserEdgeCaseTests
{
    [Fact]
    public void Exact_match_is_case_insensitive()
    {
        CmdParser.Parse(":HELP").Kind.Should().Be(TopCommand.Help);
        CmdParser.Parse(":Help").Kind.Should().Be(TopCommand.Help);
        CmdParser.Parse(":QUIT").Kind.Should().Be(TopCommand.Quit);
        CmdParser.Parse(":Toggle").Kind.Should().Be(TopCommand.Toggle);
    }

    [Fact]
    public void Prefix_match_is_case_insensitive()
    {
        CmdParser.Parse(":SEARCH term").Kind.Should().Be(TopCommand.Search);
        CmdParser.Parse(":Search term").Kind.Should().Be(TopCommand.Search);
        CmdParser.Parse(":VOL 80").Kind.Should().Be(TopCommand.Volume);
    }

    [Fact]
    public void Unknown_command_returns_Unknown()
    {
        CmdParser.Parse(":nonexistent").Kind.Should().Be(TopCommand.Unknown);
        CmdParser.Parse(":foobar 123").Kind.Should().Be(TopCommand.Unknown);
    }

    [Fact]
    public void Quit_bang_maps_to_QuitBang()
    {
        CmdParser.Parse(":quit!").Kind.Should().Be(TopCommand.QuitBang);
        CmdParser.Parse(":QUIT!").Kind.Should().Be(TopCommand.QuitBang);
    }

    [Fact]
    public void Alias_x_resolves_to_WriteQuit()
    {
        var parsed = CmdParser.Parse(":x");
        parsed.Cmd.Should().Be(":wq");
        parsed.Kind.Should().Be(TopCommand.WriteQuit);
    }

    [Fact]
    public void Alias_rm_feed_resolves_to_remove_feed()
    {
        var parsed = CmdParser.Parse(":rm-feed");
        parsed.Cmd.Should().Be(":remove-feed");
        parsed.Kind.Should().Be(TopCommand.RemoveFeed);
    }

    [Fact]
    public void Single_quoted_args_parsed_same_as_double_quoted()
    {
        var withDouble = CmdParser.Parse(":search \"hello world\"");
        var withSingle = CmdParser.Parse(":search 'hello world'");

        withSingle.Kind.Should().Be(TopCommand.Search);
        withSingle.Args.Should().Equal(withDouble.Args);
    }

    [Fact]
    public void Backslash_escape_outside_quotes_includes_next_char_literally()
    {
        // \: inside a token should produce a literal ':'
        var parsed = CmdParser.Parse(@":osd hello\:world");

        parsed.Cmd.Should().Be(":osd");
        parsed.Args.Should().Equal("hello:world");
    }

    [Fact]
    public void Command_without_args_has_empty_args_array()
    {
        var parsed = CmdParser.Parse(":toggle");

        parsed.Kind.Should().Be(TopCommand.Toggle);
        parsed.Args.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_args_are_all_captured()
    {
        var parsed = CmdParser.Parse(":opml import /some/file.opml extra");

        parsed.Kind.Should().Be(TopCommand.Opml);
        parsed.Args.Should().Equal("import", "/some/file.opml", "extra");
    }
}
