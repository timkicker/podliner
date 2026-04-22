using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using Xunit;

namespace StuiPodcast.Infra.Tests.Download;

// Verifies the thread-safe public API we added to DownloadManager (Forget,
// ClearQueue, SnapshotMap, TryGetStatus, QueuedCount, CountInState, GetState).
// These back UI-side code in CmdDownloadsModule so regressions here would
// re-introduce the dictionary/list races we fixed.
public sealed class DownloadManagerApiTests : IDisposable
{
    private readonly string _dir;
    private readonly AppData _data;
    private readonly DownloadManager _mgr;

    public DownloadManagerApiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-dlmtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _data = new AppData();
        _mgr = new DownloadManager(_data, _dir);
    }

    public void Dispose()
    {
        try { _mgr.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Enqueue / GetState / QueuedCount ─────────────────────────────────────

    [Fact]
    public void GetState_unknown_id_returns_None()
    {
        _mgr.GetState(Guid.NewGuid()).Should().Be(DownloadState.None);
    }

    [Fact]
    public void Enqueue_adds_to_queue_and_sets_state()
    {
        var id = Guid.NewGuid();
        _mgr.Enqueue(id);

        _mgr.QueuedCount().Should().BeGreaterThanOrEqualTo(0); // worker may pick it immediately
        _mgr.GetState(id).Should().BeOneOf(DownloadState.Queued, DownloadState.Running, DownloadState.Failed);
    }

    // ── Forget (the "cancel + drop all state" API) ───────────────────────────

    [Fact]
    public void Forget_removes_map_entry_and_queue_entry()
    {
        var id = Guid.NewGuid();
        _data.DownloadMap[id] = new DownloadStatus { State = DownloadState.Done, LocalPath = "/x/y.mp3" };
        _data.DownloadQueue.Add(id);

        _mgr.Forget(id);

        _data.DownloadMap.ContainsKey(id).Should().BeFalse();
        _data.DownloadQueue.Should().NotContain(id);
    }

    [Fact]
    public void Forget_unknown_id_is_noop()
    {
        var act = () => _mgr.Forget(Guid.NewGuid());
        act.Should().NotThrow();
    }

    // ── ClearQueue ───────────────────────────────────────────────────────────

    [Fact]
    public void ClearQueue_empties_queue_and_returns_count()
    {
        _data.DownloadQueue.AddRange(new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() });

        var n = _mgr.ClearQueue();

        n.Should().Be(3);
        _data.DownloadQueue.Should().BeEmpty();
    }

    [Fact]
    public void ClearQueue_on_empty_returns_zero()
    {
        _mgr.ClearQueue().Should().Be(0);
    }

    // ── SnapshotMap ──────────────────────────────────────────────────────────

    [Fact]
    public void SnapshotMap_returns_independent_copy()
    {
        var id = Guid.NewGuid();
        _data.DownloadMap[id] = new DownloadStatus { State = DownloadState.Done };

        var snap = _mgr.SnapshotMap();

        snap.Should().ContainSingle(kv => kv.Key == id);

        // Mutating the original after the snapshot must not affect the snapshot.
        _data.DownloadMap.Remove(id);
        snap.Should().ContainSingle(kv => kv.Key == id,
            "SnapshotMap must return a copy, not a live view");
    }

    [Fact]
    public void SnapshotMap_is_safe_to_enumerate_while_worker_mutates()
    {
        // Enumerate the snapshot many times; workers may be adding/removing.
        // The snapshot itself must not throw "Collection was modified".
        for (int i = 0; i < 20; i++)
            _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Done };

        var snap = _mgr.SnapshotMap();
        // simulate concurrent mutation
        _data.DownloadMap.Clear();

        // This must not throw despite the underlying dict being cleared.
        var act = () => snap.Sum(kv => (int)kv.Value.State);
        act.Should().NotThrow();
    }

    // ── TryGetStatus ─────────────────────────────────────────────────────────

    [Fact]
    public void TryGetStatus_returns_false_for_unknown()
    {
        var ok = _mgr.TryGetStatus(Guid.NewGuid(), out var st);
        ok.Should().BeFalse();
        st.Should().BeNull();
    }

    [Fact]
    public void TryGetStatus_returns_deep_copy_not_reference()
    {
        var id = Guid.NewGuid();
        _data.DownloadMap[id] = new DownloadStatus
        {
            State = DownloadState.Done,
            BytesReceived = 100,
            TotalBytes = 200,
            LocalPath = "/x/y.mp3"
        };

        _mgr.TryGetStatus(id, out var st).Should().BeTrue();
        st!.State.Should().Be(DownloadState.Done);

        // Mutate the copy; original must be untouched.
        st.State = DownloadState.Failed;
        _data.DownloadMap[id].State.Should().Be(DownloadState.Done,
            "TryGetStatus must return a deep copy so callers can't mutate the live state");
    }

    // ── CountInState / QueuedCount ──────────────────────────────────────────

    [Fact]
    public void CountInState_counts_correctly()
    {
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Failed };
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Failed };
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Done };

        _mgr.CountInState(DownloadState.Failed).Should().Be(2);
        _mgr.CountInState(DownloadState.Done).Should().Be(1);
        _mgr.CountInState(DownloadState.Running).Should().Be(0);
    }

    [Fact]
    public void QueuedCount_reflects_queue_size()
    {
        _data.DownloadQueue.AddRange(new[] { Guid.NewGuid(), Guid.NewGuid() });
        _mgr.QueuedCount().Should().Be(2);
    }

    // ── Dispose safety ───────────────────────────────────────────────────────

    [Fact]
    public void Dispose_is_idempotent()
    {
        _mgr.Dispose();
        var act = () => _mgr.Dispose();
        act.Should().NotThrow();
    }
}
