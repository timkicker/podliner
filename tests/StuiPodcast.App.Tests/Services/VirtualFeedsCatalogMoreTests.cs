using FluentAssertions;
using StuiPodcast.App.Services;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

// Extra guard tests: the virtual-feed GUIDs are persisted to appsettings.json
// as `LastSelectedFeedId`. If anyone accidentally regenerates one, existing
// user configs would silently land on a different virtual feed on next launch.
// These tests lock in the exact GUID values to prevent that.
public sealed class VirtualFeedsCatalogMoreTests
{
    [Fact]
    public void All_guid_is_stable()
        => VirtualFeedsCatalog.All.Should().Be(Guid.Parse("00000000-0000-0000-0000-00000000A11A"));

    [Fact]
    public void Saved_guid_is_stable()
        => VirtualFeedsCatalog.Saved.Should().Be(Guid.Parse("00000000-0000-0000-0000-00000000A55A"));

    [Fact]
    public void Downloaded_guid_is_stable()
        => VirtualFeedsCatalog.Downloaded.Should().Be(Guid.Parse("00000000-0000-0000-0000-00000000D0AD"));

    [Fact]
    public void History_guid_is_stable()
        => VirtualFeedsCatalog.History.Should().Be(Guid.Parse("00000000-0000-0000-0000-00000000B157"));

    [Fact]
    public void Queue_guid_is_stable()
        => VirtualFeedsCatalog.Queue.Should().Be(Guid.Parse("00000000-0000-0000-0000-00000000C0DE"));

    [Fact]
    public void Separator_guid_is_stable()
        => VirtualFeedsCatalog.Seperator.Should().Be(Guid.Parse("00000000-0000-0000-0000-00000000BEEF"));

    [Fact]
    public void All_virtual_guids_are_distinct()
    {
        var all = new[]
        {
            VirtualFeedsCatalog.All, VirtualFeedsCatalog.Saved,
            VirtualFeedsCatalog.Downloaded, VirtualFeedsCatalog.History,
            VirtualFeedsCatalog.Queue, VirtualFeedsCatalog.Seperator
        };
        all.Distinct().Should().HaveCount(all.Length);
    }

    [Fact]
    public void None_collide_with_Guid_Empty()
    {
        VirtualFeedsCatalog.All.Should().NotBe(Guid.Empty);
        VirtualFeedsCatalog.Saved.Should().NotBe(Guid.Empty);
        VirtualFeedsCatalog.Downloaded.Should().NotBe(Guid.Empty);
        VirtualFeedsCatalog.History.Should().NotBe(Guid.Empty);
        VirtualFeedsCatalog.Queue.Should().NotBe(Guid.Empty);
        VirtualFeedsCatalog.Seperator.Should().NotBe(Guid.Empty);
    }
}
