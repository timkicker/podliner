using System;

namespace StuiPodcast.Core
{
    // Rein logisch / persistierbar – keine Transport-Details
    public enum DownloadState
    {
        None = 0,       // nicht markiert
        Queued = 1,     // markiert/steht an
        Running = 2,    // lädt gerade
        Verifying = 3,  // Hash/Datei prüfen (später nutzbar)
        Done = 4,       // erfolgreich lokal vorhanden
        Failed = 5,     // fehlgeschlagen (letzter Versuch)
        Canceled = 6    // abgebrochen
    }

    // Optional: spätere Telemetrie/Anzeigen (nicht zwingend nötig)
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