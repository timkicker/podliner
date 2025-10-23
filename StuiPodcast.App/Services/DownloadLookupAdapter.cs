using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Services;

// ==========================================================
// Adapter for download read-model into AppFacade
// ==========================================================
sealed class DownloadLookupAdapter : AppFacade.ILocalDownloadLookup
{
    private readonly DownloadManager _mgr;
    private readonly AppData _data;

    public DownloadLookupAdapter(DownloadManager mgr, AppData data)
    {
        _mgr = mgr;
        _data = data;
    }

    public bool IsDownloaded(Guid episodeId)
        => TryGetLocalPath(episodeId, out _);

    public bool TryGetLocalPath(Guid episodeId, out string? path)
    {
        path = null;
        if (_data.DownloadMap.TryGetValue(episodeId, out var st) &&
            st.State == DownloadState.Done &&
            !string.IsNullOrWhiteSpace(st.LocalPath) &&
            File.Exists(st.LocalPath))
        {
            path = st.LocalPath;
            return true;
        }
        return false;
    }
}