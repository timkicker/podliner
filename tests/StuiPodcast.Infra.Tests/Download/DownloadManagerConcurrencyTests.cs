using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.Infra.Tests.Download;

// Stress tests for the thread-safe public API. These would have thrown
// "Collection was modified" or corrupted state under the pre-fix code that
// had UI-thread mutations outside the download manager's _gate lock.
public sealed class DownloadManagerConcurrencyTests : IDisposable
{
    private readonly string _dir;
    private readonly AppData _data = new();
    private readonly LibraryStore _lib;
    private readonly DownloadManager _mgr;

    public DownloadManagerConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-dlconc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lib = new LibraryStore(_dir);
        _lib.Load();
        _mgr = new DownloadManager(_data, _lib, _dir);
    }

    public void Dispose()
    {
        try { _mgr.Dispose(); } catch { }
        try { _lib.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Concurrent_SnapshotMap_while_mutating_via_API_does_not_throw()
    {
        // One thread mutates via the thread-safe API (_mgr.Enqueue / _mgr.Forget),
        // another reads via SnapshotMap. Both take _gate, so no collection
        // modification exceptions should surface.
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                _mgr.Enqueue(ids[i % ids.Length]);
                _mgr.Forget(ids[(i + 7) % ids.Length]);
                i++;
            }
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _mgr.SnapshotMap();
                _mgr.QueuedCount();
                _mgr.CountInState(DownloadState.Queued);
                _mgr.CountInState(DownloadState.Canceled);
            }
        });

        var act = async () => await Task.WhenAll(writer, reader);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Concurrent_ClearQueue_and_Enqueue_does_not_corrupt()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var enqueuer = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _mgr.Enqueue(Guid.NewGuid());
            }
        });

        var clearer = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _mgr.ClearQueue();
            }
        });

        var act = async () => await Task.WhenAll(enqueuer, clearer);
        await act.Should().NotThrowAsync();

        // Final invariant: queue count must match the actual list length.
        // If ClearQueue/Enqueue raced unsafely, these could diverge.
        _mgr.QueuedCount().Should().Be(_data.DownloadQueue.Count);
    }

    [Fact]
    public async Task Many_parallel_Forget_calls_are_safe()
    {
        // Populate with many entries.
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            _data.DownloadMap[id] = new DownloadStatus { State = DownloadState.Done };

        // Forget them all in parallel.
        var tasks = ids.Select(id => Task.Run(() => _mgr.Forget(id))).ToArray();
        await Task.WhenAll(tasks);

        _data.DownloadMap.Should().BeEmpty();
    }
}
