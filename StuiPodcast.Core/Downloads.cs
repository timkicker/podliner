namespace StuiPodcast.Core
{
    // logical / persistable only
    // no transport details
    public enum DownloadState
    {
        None = 0,       // unmarked
        Queued = 1,     // queued
        Running = 2,    // downloading
        Verifying = 3,  // verifying (hash/file)
        Done = 4,       // completed (local file present)
        Failed = 5,     // failed (last attempt)
        Canceled = 6    // canceled
    }

    // optional telemetry / ui hints (not required)
    public sealed class DownloadStatus
    {
        public DownloadState State { get; set; } = DownloadState.None;
        public long BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
        public string? LocalPath { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}