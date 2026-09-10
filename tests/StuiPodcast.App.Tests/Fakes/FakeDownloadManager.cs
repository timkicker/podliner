using StuiPodcast.Core;
using StuiPodcast.Infra.Download;

namespace StuiPodcast.App.Tests.Fakes;

// In-memory stand-in for DownloadManager: no HTTP, no worker thread, no
// filesystem. Records the control calls and lets a test drive StatusChanged
// to simulate a download progressing.
sealed class FakeDownloadManager : IDownloadManager
{
    public event Action<Guid, DownloadStatus>? StatusChanged;

    private readonly Dictionary<Guid, DownloadStatus> _map = new();
    private readonly List<Guid> _queue = new();

    public int EnsureRunningCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool Disposed { get; private set; }
    public readonly List<Guid> Enqueued = new();
    public readonly List<Guid> Fronted = new();
    public readonly List<Guid> Cancelled = new();
    public readonly List<Guid> Forgotten = new();

    public string Root { get; set; } = "/home/tim/Podcasts";

    public void EnsureRunning() => EnsureRunningCalls++;
    public void Stop() => StopCalls++;
    public void Dispose() => Disposed = true;

    public void Enqueue(Guid episodeId)
    {
        Enqueued.Add(episodeId);
        if (!_queue.Contains(episodeId)) _queue.Add(episodeId);
        Set(episodeId, DownloadState.Queued);
    }

    public void ForceFront(Guid episodeId)
    {
        Fronted.Add(episodeId);
        _queue.Remove(episodeId);
        _queue.Insert(0, episodeId);
        Set(episodeId, DownloadState.Queued);
    }

    public void Cancel(Guid episodeId)
    {
        Cancelled.Add(episodeId);
        _queue.Remove(episodeId);
        Set(episodeId, DownloadState.Canceled);
    }

    public void Forget(Guid episodeId)
    {
        Forgotten.Add(episodeId);
        _queue.Remove(episodeId);
        _map.Remove(episodeId);
    }

    public int ClearQueue()
    {
        var n = _queue.Count;
        foreach (var id in _queue.ToList()) Set(id, DownloadState.Canceled);
        _queue.Clear();
        return n;
    }

    public int QueuedCount() => _queue.Count;

    public int CountInState(DownloadState state) => _map.Values.Count(s => s.State == state);

    public DownloadState GetState(Guid episodeId)
        => _map.TryGetValue(episodeId, out var s) ? s.State : DownloadState.None;

    public bool TryGetStatus(Guid episodeId, out DownloadStatus? status)
    {
        var ok = _map.TryGetValue(episodeId, out var s);
        status = s;
        return ok;
    }

    public IReadOnlyList<KeyValuePair<Guid, DownloadStatus>> SnapshotMap() => _map.ToList();

    public string CurrentDownloadRoot() => Root;

    // ── test drivers ────────────────────────────────────────────────────────

    // Sets a state and raises StatusChanged, the way the real worker does.
    public void Set(Guid id, DownloadState state, long got = 0, long? total = null, string? localPath = null)
    {
        var st = new DownloadStatus
        {
            State = state,
            BytesReceived = got,
            TotalBytes = total,
            LocalPath = localPath,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _map[id] = st;
        StatusChanged?.Invoke(id, st);
    }

    // A progress pulse: same state, more bytes. The real manager emits these
    // roughly every 400ms.
    public void Pulse(Guid id, long got, long total)
        => Set(id, DownloadState.Running, got, total);
}
