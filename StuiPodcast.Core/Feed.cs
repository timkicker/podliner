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
    }
}