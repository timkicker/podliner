namespace StuiPodcast.Core.Sync;

public sealed class PendingGpodderAction
{
    public string         PodcastUrl { get; set; } = "";
    public string         EpisodeUrl { get; set; } = "";
    public string?        Guid       { get; set; }
    public string         Action     { get; set; } = "play";
    public DateTimeOffset Timestamp  { get; set; }
    public int?           Started    { get; set; }
    public int?           Position   { get; set; }   // seconds
    public int?           Total      { get; set; }   // seconds
}
