using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Services;

// Answers "is this episode downloaded?" on hot rendering paths. Naive version
// hits File.Exists per call, which adds up when rendering the "Downloaded"
// virtual feed with thousands of episodes. This caches successful verifications
// and invalidates on download state changes; File.Exists is re-run on a TTL
// to catch external file deletions.
sealed class DownloadLookupAdapter : AppFacade.ILocalDownloadLookup, IDisposable
{
    private readonly DownloadManager _mgr;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CacheEntry> _verified = new();
    private static readonly TimeSpan VerifyTtl = TimeSpan.FromMinutes(1);

    private readonly record struct CacheEntry(string Path, DateTimeOffset CheckedAt);

    public DownloadLookupAdapter(DownloadManager mgr, AppData data)
    {
        _mgr = mgr;
        _mgr.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(Guid id, DownloadStatus st)
    {
        lock (_gate)
        {
            if (st.State == DownloadState.Done && !string.IsNullOrEmpty(st.LocalPath))
                _verified[id] = new CacheEntry(st.LocalPath, DateTimeOffset.UtcNow);
            else
                _verified.Remove(id); // anything non-Done invalidates
        }
    }

    public bool IsDownloaded(Guid episodeId)
        => TryGetLocalPath(episodeId, out _);

    public bool TryGetLocalPath(Guid episodeId, out string? path)
    {
        path = null;

        // Cache path: most common case during list rendering.
        lock (_gate)
        {
            if (_verified.TryGetValue(episodeId, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.CheckedAt < VerifyTtl)
                {
                    path = entry.Path;
                    return true;
                }
                // TTL expired: re-verify with File.Exists.
                if (File.Exists(entry.Path))
                {
                    _verified[episodeId] = new CacheEntry(entry.Path, DateTimeOffset.UtcNow);
                    path = entry.Path;
                    return true;
                }
                _verified.Remove(episodeId); // file deleted externally
            }
        }

        // Cold path: post-startup before StatusChanged ran, or after cache invalidation.
        // Consults the download manager and caches the result on success.
        if (_mgr.TryGetStatus(episodeId, out var st) && st != null &&
            st.State == DownloadState.Done &&
            !string.IsNullOrWhiteSpace(st.LocalPath) &&
            File.Exists(st.LocalPath))
        {
            lock (_gate) _verified[episodeId] = new CacheEntry(st.LocalPath, DateTimeOffset.UtcNow);
            path = st.LocalPath;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        try { _mgr.StatusChanged -= OnStatusChanged; } catch { }
        lock (_gate) _verified.Clear();
    }
}
