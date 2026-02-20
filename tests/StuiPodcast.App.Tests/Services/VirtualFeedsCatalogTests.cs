using FluentAssertions;
using StuiPodcast.App.Services;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class VirtualFeedsCatalogTests
{
    static readonly Guid[] AllIds =
    [
        VirtualFeedsCatalog.All,
        VirtualFeedsCatalog.Saved,
        VirtualFeedsCatalog.Downloaded,
        VirtualFeedsCatalog.History,
        VirtualFeedsCatalog.Queue,
        VirtualFeedsCatalog.Seperator,
    ];

    [Fact]
    public void All_virtual_feed_ids_are_distinct()
    {
        AllIds.Distinct().Should().HaveCount(AllIds.Length);
    }

    [Fact]
    public void No_virtual_feed_id_is_empty_guid()
    {
        AllIds.Should().NotContain(Guid.Empty);
    }

    [Fact]
    public void Virtual_feed_ids_do_not_collide_with_each_other_individually()
    {
        // Spot-check a few well-known pairs
        VirtualFeedsCatalog.All.Should().NotBe(VirtualFeedsCatalog.Queue);
        VirtualFeedsCatalog.History.Should().NotBe(VirtualFeedsCatalog.Downloaded);
        VirtualFeedsCatalog.Saved.Should().NotBe(VirtualFeedsCatalog.Seperator);
    }
}
