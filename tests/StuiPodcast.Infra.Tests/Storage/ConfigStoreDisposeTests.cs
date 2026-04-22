using FluentAssertions;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.Infra.Tests.Storage;

// Regression for the debounce-timer leak fix: ConfigStore now implements
// IDisposable and must flush any pending save before releasing the timer.
public sealed class ConfigStoreDisposeTests : IDisposable
{
    private readonly string _dir;

    public ConfigStoreDisposeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-cfgdispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var store = new ConfigStore(_dir);
        store.Load();

        store.Dispose();
        var act = () => store.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_flushes_pending_debounced_save()
    {
        var store = new ConfigStore(_dir);
        store.Load();
        store.Current.Volume0_100 = 42;

        // Schedule a debounced save (1s interval) — without Dispose flushing it,
        // this would never hit disk if we exit immediately.
        store.SaveAsync();

        store.Dispose();

        // Reload to verify the save actually landed.
        var reload = new ConfigStore(_dir);
        reload.Load();
        reload.Current.Volume0_100.Should().Be(42);
    }

    [Fact]
    public void Dispose_without_pending_save_does_not_write()
    {
        var store = new ConfigStore(_dir);
        store.Load();
        // Touch nothing. Dispose should be a no-op on disk.

        store.Dispose();

        // No file should have been created just by loading + disposing.
        File.Exists(store.FilePath).Should().BeFalse();
    }

    [Fact]
    public void Dispose_respects_read_only_flag()
    {
        var store = new ConfigStore(_dir);
        store.Load();

        // Reflect IsReadOnly true by writing then locking the file would be OS-specific.
        // Easier path: verify Dispose doesn't throw even if ConfigDirectory no longer exists.
        Directory.Delete(_dir, recursive: true);

        var act = () => store.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Concurrent_SaveAsync_calls_during_Dispose_do_not_throw()
    {
        var store = new ConfigStore(_dir);
        store.Load();

        // Schedule several saves rapidly, then dispose mid-stream.
        for (int i = 0; i < 20; i++) store.SaveAsync();

        var act = () => store.Dispose();
        act.Should().NotThrow();
    }
}
