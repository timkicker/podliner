using Podliner.Core;

namespace Podliner.Infra.Download;

// The download queue as its callers see it. Extracted so DownloadUseCase,
// UiDownloaderBridge and DownloadLookupAdapter can be tested without a real
// HttpClient, a worker thread or anything touching the filesystem.
//
// This is only the control surface; the transport, retry policy, index
// persistence and progress pulses stay inside DownloadManager.
public interface IDownloadManager : IDisposable
{
    // Fires on every state transition and, while a download runs, roughly
    // every 400ms as a progress pulse. Subscribers must treat repeated
    // Running events as progress rather than as a new download.
    event Action<Guid, DownloadStatus>? StatusChanged;

    // Starts the worker if it is not already running. Safe to call repeatedly.
    void EnsureRunning();
    void Stop();

    void Enqueue(Guid episodeId);
    void ForceFront(Guid episodeId);
    void Cancel(Guid episodeId);
    void Forget(Guid episodeId);
    int ClearQueue();

    int QueuedCount();
    int CountInState(DownloadState state);
    DownloadState GetState(Guid episodeId);
    bool TryGetStatus(Guid episodeId, out DownloadStatus? status);
    IReadOnlyList<KeyValuePair<Guid, DownloadStatus>> SnapshotMap();

    // Directory episodes are written to, after the config override and the
    // platform default have been resolved.
    string CurrentDownloadRoot();
}
