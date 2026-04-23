namespace StuiPodcast.Core
{
    // podcast feed metadata (canonical url)
    public class Feed
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;

        // canonical feed url (absolute http/https)
        public string Url { get; set; } = string.Empty;

        public DateTimeOffset? LastChecked { get; set; }

        // Conditional-GET hints from the last successful fetch. Sent back as
        // If-None-Match / If-Modified-Since so unchanged feeds return 304
        // without a body — most publishers support one or both.
        public string? LastEtag { get; set; }
        public string? LastModified { get; set; }

        // Per-feed playback overrides. Null = inherit app defaults.
        // SpeedOverride is applied on Play(); auto-download queues fresh
        // episodes from the refresh loop (subject to queue dedup).
        public double? SpeedOverride { get; set; }
        public bool AutoDownload { get; set; }
    }
}