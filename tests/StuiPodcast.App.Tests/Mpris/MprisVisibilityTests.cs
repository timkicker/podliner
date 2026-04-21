using FluentAssertions;
using StuiPodcast.App.Mpris;
using Xunit;

namespace StuiPodcast.App.Tests.Mpris;

// Regression tests for issue #23: Tmds.DBus builds proxies in a separate
// dynamic assembly via reflection. If these types are not public, proxy
// construction fails with TypeAccessException and MPRIS stops working.
public sealed class MprisVisibilityTests
{
    [Fact]
    public void IMprisMediaPlayer2_must_be_public()
    {
        typeof(IMprisMediaPlayer2).IsPublic.Should().BeTrue(
            "Tmds.DBus requires public visibility to build runtime proxies; " +
            "see issue #23 — making this internal breaks MPRIS media keys on Linux.");
    }

    [Fact]
    public void IMprisPlayer_must_be_public()
    {
        typeof(IMprisPlayer).IsPublic.Should().BeTrue(
            "Tmds.DBus requires public visibility to build runtime proxies; " +
            "see issue #23 — making this internal breaks MPRIS media keys on Linux.");
    }

    [Fact]
    public void MprisObject_must_be_public()
    {
        typeof(MprisObject).IsPublic.Should().BeTrue(
            "MprisObject implements public D-Bus interfaces and is registered " +
            "with Connection.RegisterObjectAsync — must be public for Tmds.DBus.");
    }

    [Fact]
    public void PlaybackCoordinator_must_be_public()
    {
        // Required because it appears in MprisObject's public constructor signature.
        typeof(PlaybackCoordinator).IsPublic.Should().BeTrue(
            "PlaybackCoordinator is a constructor parameter of the public MprisObject.");
    }
}
