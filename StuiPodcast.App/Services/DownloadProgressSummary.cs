using StuiPodcast.Core;

namespace StuiPodcast.App.Services;

// Turns the per-episode download states into the single figure shown in the
// window badge. Extracted out of UiDownloaderBridge so the arithmetic is
// testable without a DownloadManager or a UI.
//
// The percentage is weighted by bytes rather than by item count, so one
// 500 MB episode does not read the same as one 5 MB episode. Items whose
// total size the server never announced carry no weight; when none of the
// items has a known size the summary falls back to counting finished items,
// which at least moves in the right direction.
internal static class DownloadProgressSummary
{
    public readonly record struct Item(long BytesReceived, long? TotalBytes, DownloadState State);

    public static int Percent(IEnumerable<Item> items)
    {
        long sumBytes = 0;
        long sumTotal = 0;
        int done = 0;
        int total = 0;

        foreach (var it in items)
        {
            total++;
            if (it.State == DownloadState.Done) done++;

            if (it.TotalBytes is not { } t || t <= 0) continue;

            if (it.State == DownloadState.Done)
            {
                sumBytes += t;
                sumTotal += t;
            }
            else if (it.State is DownloadState.Running or DownloadState.Verifying)
            {
                sumBytes += Math.Clamp(it.BytesReceived, 0, t);
                sumTotal += t;
            }
        }

        if (sumTotal > 0)
            return (int)Math.Round(100.0 * sumBytes / sumTotal);

        return total == 0 ? 0 : (int)Math.Round(100.0 * done / Math.Max(1, total));
    }
}
