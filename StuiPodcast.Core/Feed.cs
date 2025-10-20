using System;

namespace StuiPodcast.Core
{
    /// <summary>
    /// Podcast-Feed-Metadaten (kanonische URL).
    /// </summary>
    public class Feed
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;

        /// <summary>Kanonische Feed-URL (http/https, absolut).</summary>
        public string Url { get; set; } = string.Empty;

        public DateTimeOffset? LastChecked { get; set; }
    }
}