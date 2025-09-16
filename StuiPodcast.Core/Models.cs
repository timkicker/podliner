using System;

namespace StuiPodcast.Core;

public class Feed {
    public Guid   Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset? LastChecked { get; set; }
}

public class Episode {
    public Guid   Id { get; set; } = Guid.NewGuid();
    public Guid   FeedId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset? PubDate { get; set; }
    public string AudioUrl { get; set; } = "";
    public TimeSpan? Duration { get; set; }
    public string DescriptionText { get; set; } = "";
}

public class PlayerState {
    public Guid? EpisodeId { get; set; }
    public bool  IsPlaying { get; set; }
    public int   Volume0_100 { get; set; } = 70;
    public double Speed { get; set; } = 1.0;
    public TimeSpan Position { get; set; }
    public TimeSpan? Length { get; set; }
}

public class AppData {
    public List<Feed> Feeds { get; set; } = new();
    public List<Episode> Episodes { get; set; } = new();
}
