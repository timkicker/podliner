using FluentAssertions;
using StuiPodcast.App.Command;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdSyncParserTests
{
    [Fact]
    public void Bare_sync_maps_to_Sync()
    {
        var parsed = CmdParser.Parse(":sync");

        parsed.Kind.Should().Be(TopCommand.Sync);
        parsed.Args.Should().BeEmpty();
    }

    [Fact]
    public void Sync_login_captures_all_args()
    {
        var parsed = CmdParser.Parse(":sync login https://gpodder.net user pass");

        parsed.Kind.Should().Be(TopCommand.Sync);
        parsed.Args.Should().Equal("login", "https://gpodder.net", "user", "pass");
    }

    [Fact]
    public void Sync_is_case_insensitive()
    {
        var parsed = CmdParser.Parse(":SYNC status");

        parsed.Kind.Should().Be(TopCommand.Sync);
    }

    [Fact]
    public void Sync_status_subcommand_in_args()
    {
        var parsed = CmdParser.Parse(":sync status");

        parsed.Kind.Should().Be(TopCommand.Sync);
        parsed.Args[0].Should().Be("status");
    }

    [Fact]
    public void Sync_auto_on_subcommand_in_args()
    {
        var parsed = CmdParser.Parse(":sync auto on");

        parsed.Kind.Should().Be(TopCommand.Sync);
        parsed.Args.Should().Equal("auto", "on");
    }
}
