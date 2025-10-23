namespace StuiPodcast.Core
{
    // persisted library (library/library.json)
    // holds feeds, episodes, queue, history
    // no download data here
    public sealed class Library
    {
        public int SchemaVersion { get; set; } = 1;

        // allow set; so system.text.json can populate
        public List<Feed>    Feeds    { get; set; } = new();
        public List<Episode> Episodes { get; set; } = new();

        // playback queue (episode ids, fifo)
        public List<Guid> Queue { get; set; } = new();

        // recently played / completed
        public List<HistoryItem> History { get; set; } = new();
    }

    public sealed class HistoryItem
    {
        public Guid EpisodeId { get; set; }
        public DateTimeOffset At { get; set; }
    }
}