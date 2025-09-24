using System;
using Terminal.Gui;

namespace StuiPodcast.Core;

public class Feed {
    public Guid   Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset? LastChecked { get; set; }
}

public class Episode {
    public bool Saved { get; set; } = false;
    public bool Downloaded { get; set; } = false;
    // in Episode.cs (oder wo dein Episode-Modell liegt)
    public long? LastPosMs { get; set; }      // letzte Position
    public long? LengthMs  { get; set; }      // bekannte Länge
    public bool  Played    { get; set; }      // als gespielt markiert
    public DateTimeOffset? LastPlayedAt { get; set; } // optionales Meta

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
    
    // --- Sortierung (global) ---
    // AppData.cs
    public string PlaySource { get; set; } = "auto"; // "auto" | "local" | "remote"
    public bool   NetworkOnline { get; set; } = true;

    
    public List<Guid> Queue { get; set; } = new();

    public string? DownloadDir { get; set; }          // z.B. ~/Podcasts
    public List<Guid> DownloadQueue { get; set; } = new();  // FIFO
    public Dictionary<Guid, StuiPodcast.Core.DownloadStatus> DownloadMap { get; set; } = new();

    
    public int HistorySize { get; set; } = 200;
    public string SortBy  { get; set; } = "pubdate"; // pubdate|title|played|progress|feed
    public string SortDir { get; set; } = "desc";    // asc|desc

    
    TabView.Tab? episodesTabRef = null; // <— Referenz auf „Episodes“-Tab
    // AppData.cs – Ergänzungen
    public bool AutoAdvance  { get; set; } = true;      // automatisch weiter zur nächsten Episode
    public bool WrapAdvance  { get; set; } = true;      // am Listenende wieder vorne anfangen
    public int  PlayedThresholdPercent { get; set; } = 95; // ab x% als „gespielt“ markieren


    
    public bool UnplayedOnly { get; set; } = false;
    public Dictionary<Guid, int> LastSelectedEpisodeIndexByFeed { get; set; } = new();
    public int  Volume0_100 { get; set; } = 50;
    public double Speed { get; set; } = 1.0;

    public int? LastVolume0_100 { get; set; }
    public double? LastSpeed { get; set; }


    public Guid? LastSelectedFeedId { get; set; }
    public int?  LastSelectedEpisodeIndex { get; set; }

    public bool PlayerAtTop { get; set; } = false;
    public List<Feed> Feeds { get; set; } = new();
    public List<Episode> Episodes { get; set; } = new();
}
