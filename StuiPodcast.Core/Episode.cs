namespace StuiPodcast.Core
{
    // episode metadata + usage state
    // no download status and no local path here
    public class Episode
    {
        // identity & association
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid FeedId { get; set; }

        // primary identity of the audio resource (http/https, canonicalized)
        public string AudioUrl { get; set; } = string.Empty;

        // optional rss guid from the item; used to match across url changes
        public string? RssGuid { get; set; }

        // metadata
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset? PubDate { get; set; }
        public long DurationMs { get; set; } = 0;         // 0 if unknown
        public string DescriptionText { get; set; } = string.Empty;

        // usage flags
        public bool Saved { get; set; } = false;          // ★ favorite

        // progress and last playback timestamp
        public EpisodeProgress Progress { get; set; } = new();

        // ui-only: manually marked as played (not set automatically)
        public bool ManuallyMarkedPlayed { get; set; } = false;

        // Podcast-2.0 chapters. Url points to a JSON file (application/json+chapters)
        // per the podcast namespace spec. Chapters is filled lazily the
        // first time it's requested — we don't fetch during refresh because
        // that would block the sequential feed pass and a lot of feeds
        // either don't have chapters or have them on a slow host.
        public string? ChaptersUrl { get; set; }
        public List<Chapter> Chapters { get; set; } = new();
    }

    public sealed class Chapter
    {
        // Start offset from episode start.
        public double StartSeconds { get; set; }
        public string Title { get; set; } = string.Empty;
        // Optional extra fields from the JSON — kept so we can display them
        // later (clickable URL, cover art) without a model migration.
        public string? Url { get; set; }
        public string? Img { get; set; }
    }

    public sealed class EpisodeProgress
    {
        // last known position in ms; invariant: 0 ≤ lastposms ≤ durationms
        public long LastPosMs { get; set; } = 0;

        // last playback instant (utc recommended), or null
        public DateTimeOffset? LastPlayedAt { get; set; }
    }
}