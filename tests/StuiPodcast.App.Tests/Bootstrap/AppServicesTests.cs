using FluentAssertions;
using StuiPodcast.App.Bootstrap;
using System.Reflection;
using Xunit;

namespace StuiPodcast.App.Tests.Bootstrap;

// AppServices is the composition-root record that replaced the reflection-
// based service locator pattern. These tests guard the contract so future
// refactors don't silently regress back to reaching into Program.cs statics.
public sealed class AppServicesTests
{
    [Fact]
    public void Is_sealed()
    {
        // Prevents accidental subclassing that would let test doubles bypass
        // the explicit wiring contract.
        typeof(AppServices).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void Exposes_every_composition_member_the_ui_wiring_needs()
    {
        // If someone removes a member here we catch it before WireUi crashes.
        var members = typeof(AppServices)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        new[] { "Ui", "Data", "App", "ConfigStore", "LibraryStore", "Feeds",
                "Player", "Playback", "Downloader", "DownloadLookup", "MemLog",
                "GpodderStore", "Gpodder", "Saver", "Net", "EngineSvc" }
            .Should().BeSubsetOf(members);
    }

    [Fact]
    public void UiComposer_has_no_reflection_into_Program()
    {
        // Regression guard: UiComposer used to do typeof(Program).GetField(...)
        // on private statics. Once we migrated to AppServices this dependency
        // disappeared and should never come back.
        var source = System.IO.File.ReadAllText(
            ResolvePath("StuiPodcast.App/UI/UiComposer.cs"));

        source.Should().NotContain("typeof(Program).GetField",
            "UiComposer must resolve services through AppServices, not reflection");
    }

    private static string ResolvePath(string rel)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StuiPodcast.App", "UI", "UiComposer.cs")))
            dir = dir.Parent;
        if (dir == null) throw new System.IO.FileNotFoundException("UiComposer.cs not found walking up from " + AppContext.BaseDirectory);
        return System.IO.Path.Combine(dir.FullName, rel);
    }
}
