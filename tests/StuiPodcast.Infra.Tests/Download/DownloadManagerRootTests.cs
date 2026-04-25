using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.Infra.Tests.Download;

// Regression: appsettings.json's DownloadDir override used to be silently
// ignored AND clobbered by ResolveDownloadRoot writing the default back
// into AppData. These tests pin down the new behaviour.
public sealed class DownloadManagerRootTests : IDisposable
{
    readonly string _dir;
    readonly LibraryStore _lib;

    public DownloadManagerRootTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-dlroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lib = new LibraryStore(_dir);
        _lib.Load();
    }

    public void Dispose()
    {
        try { _lib.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void CurrentDownloadRoot_honours_AppData_override()
    {
        var custom = Path.Combine(_dir, "my-custom-podcast-folder");
        var data = new AppData { DownloadDir = custom };
        using var mgr = new DownloadManager(data, _lib, _dir);

        mgr.CurrentDownloadRoot().Should().Be(custom);
        Directory.Exists(custom).Should().BeTrue("ResolveDownloadRoot ensures the directory exists");
    }

    [Fact]
    public void CurrentDownloadRoot_does_not_mutate_AppData_when_using_default()
    {
        var data = new AppData(); // DownloadDir == null → use platform default
        using var mgr = new DownloadManager(data, _lib, _dir);

        var root = mgr.CurrentDownloadRoot();
        root.Should().NotBeNullOrWhiteSpace();
        // Critical regression check: must not write the default path back
        // into AppData. Otherwise it gets persisted to appsettings.json and
        // a later override edit looks "ignored" because the field is taken.
        data.DownloadDir.Should().BeNull("the default path must stay implicit");
    }

    [Fact]
    public void CurrentDownloadRoot_keeps_override_intact_across_calls()
    {
        var custom = Path.Combine(_dir, "x");
        var data = new AppData { DownloadDir = custom };
        using var mgr = new DownloadManager(data, _lib, _dir);

        mgr.CurrentDownloadRoot();
        mgr.CurrentDownloadRoot();
        data.DownloadDir.Should().Be(custom);
    }
}
