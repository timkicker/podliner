using FluentAssertions;
using StuiPodcast.App.Command;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CommandDispatcherTests
{
    [Theory]
    [InlineData(":help",        "Help")]
    [InlineData(":quit",        "Quit")]
    [InlineData(":quit!",       "QuitBang")]
    [InlineData(":toggle",      "Toggle")]
    [InlineData(":next",        "Next")]
    [InlineData(":prev",        "Prev")]
    [InlineData(":play-next",   "PlayNext")]
    [InlineData(":play-prev",   "PlayPrev")]
    [InlineData(":now",         "Now")]
    [InlineData(":write",       "Write")]
    [InlineData(":wq",          "WriteQuit")]
    [InlineData(":add",         "AddFeed")]
    [InlineData(":refresh",     "Refresh")]
    [InlineData(":remove-feed", "RemoveFeed")]
    public void Parse_exact_commands(string input, string expected)
    {
        CmdParser.Parse(input).Kind.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(":engine vlc",     "Engine")]
    [InlineData(":seek +30",       "Seek")]
    [InlineData(":vol 80",         "Volume")]
    [InlineData(":speed 1.5",      "Speed")]
    [InlineData(":replay 10",      "Replay")]
    [InlineData(":goto top",       "Goto")]
    [InlineData(":save on",        "Save")]
    [InlineData(":sort by title",  "Sort")]
    [InlineData(":filter unplayed","Filter")]
    [InlineData(":net offline",    "Net")]
    [InlineData(":feed all",       "Feed")]
    [InlineData(":history clear",  "History")]
    [InlineData(":opml import x",  "Opml")]
    [InlineData(":sync login a b c", "Sync")]
    [InlineData(":theme base",     "Theme")]
    [InlineData(":search foo",     "Search")]
    [InlineData(":jump 1:30",      "Jump")]
    [InlineData(":copy",           "Copy")]
    [InlineData(":open",           "Open")]
    [InlineData(":logs",           "Logs")]
    [InlineData(":osd test",       "Osd")]
    [InlineData(":play-source local", "PlaySource")]
    [InlineData(":update",         "Refresh")]
    [InlineData(":next-unplayed",  "NextUnplayed")]
    [InlineData(":prev-unplayed",  "PrevUnplayed")]
    public void Parse_prefix_commands(string input, string expected)
    {
        CmdParser.Parse(input).Kind.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(":h",       ":help")]
    [InlineData(":q",       ":quit")]
    [InlineData(":q!",      ":quit!")]
    [InlineData(":w",       ":write")]
    [InlineData(":x",       ":wq")]
    [InlineData(":a",       ":add")]
    [InlineData(":r",       ":refresh")]
    [InlineData(":rm-feed", ":remove-feed")]
    public void Parse_aliases_canonicalize(string input, string expectedCmd)
    {
        CmdParser.Parse(input).Cmd.Should().Be(expectedCmd);
    }

    [Theory]
    [InlineData(":HELP")]
    [InlineData(":Help")]
    [InlineData(":TOGGLE")]
    [InlineData(":Seek +10")]
    public void Parse_is_case_insensitive(string input)
    {
        CmdParser.Parse(input).Kind.ToString().Should().NotBe("Unknown");
    }

    [Theory]
    [InlineData(":bogus")]
    [InlineData(":foobar")]
    [InlineData(":xyz something")]
    public void Parse_unknown_commands(string input)
    {
        CmdParser.Parse(input).Kind.ToString().Should().Be("Unknown");
    }

    [Fact]
    public void Parse_args_are_split_correctly()
    {
        var parsed = CmdParser.Parse(":sync login server user pass");
        parsed.Kind.ToString().Should().Be("Sync");
        parsed.Args.Should().Equal("login", "server", "user", "pass");
    }

    [Fact]
    public void Parse_without_colon_adds_colon()
    {
        var parsed = CmdParser.Parse("toggle");
        parsed.Cmd.Should().Be(":toggle");
        parsed.Kind.ToString().Should().Be("Toggle");
    }

    [Fact]
    public void Parse_vim_shortcuts()
    {
        CmdParser.Parse(":zt").Kind.ToString().Should().Be("VimTop");
        CmdParser.Parse(":zz").Kind.ToString().Should().Be("VimMiddle");
        CmdParser.Parse(":zb").Kind.ToString().Should().Be("VimBottom");
        CmdParser.Parse(":H").Kind.ToString().Should().Be("VimTop");
        CmdParser.Parse(":M").Kind.ToString().Should().Be("VimMiddle");
        CmdParser.Parse(":L").Kind.ToString().Should().Be("VimBottom");
    }

    [Fact]
    public void Parse_lowercase_h_still_maps_to_help()
    {
        CmdParser.Parse(":h").Kind.ToString().Should().Be("Help");
    }

    [Fact]
    public void Parse_single_quoted_args()
    {
        var parsed = CmdParser.Parse(":search 'hello world'");
        parsed.Args.Should().Equal("hello world");
    }

    [Fact]
    public void Parse_escaped_characters()
    {
        var parsed = CmdParser.Parse(":osd hello\\ world");
        parsed.Args.Should().Equal("hello world");
    }
}
