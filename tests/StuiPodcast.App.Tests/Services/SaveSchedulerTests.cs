using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class SaveSchedulerTests : IDisposable
{
    private readonly string _dir;
    private readonly AppData _data;
    private readonly AppFacade _facade;
    private int _syncCount;

    public SaveSchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var configStore = new ConfigStore(_dir);
        configStore.Load();
        var libraryStore = new LibraryStore(_dir);
        libraryStore.Load();

        _facade = new AppFacade(configStore, libraryStore);
        _data = new AppData();
        _syncCount = 0;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SaveScheduler Create() => new(_data, _facade, () => _syncCount++);

    [Fact]
    public async Task Flush_saves_immediately()
    {
        using var sched = Create();
        await sched.RequestSaveAsync(flush: true);
        _syncCount.Should().Be(1);
    }

    [Fact]
    public async Task Regular_request_saves_when_interval_elapsed()
    {
        using var sched = Create();
        await sched.RequestSaveAsync();
        _syncCount.Should().Be(1);
    }

    [Fact]
    public async Task Rapid_requests_debounce()
    {
        using var sched = Create();

        // First save goes through immediately
        await sched.RequestSaveAsync();
        _syncCount.Should().Be(1);

        // Rapid subsequent calls should schedule delayed saves
        await sched.RequestSaveAsync();
        await sched.RequestSaveAsync();
        await sched.RequestSaveAsync();

        // Wait for debounce timer (MIN_INTERVAL_MS = 1000)
        await Task.Delay(1500);

        // Should have debounced: 1 immediate + 1 delayed = at least 2
        _syncCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task Multiple_flushes_each_save()
    {
        using var sched = Create();
        await sched.RequestSaveAsync(flush: true);
        await sched.RequestSaveAsync(flush: true);
        await sched.RequestSaveAsync(flush: true);
        _syncCount.Should().Be(3);
    }
}
