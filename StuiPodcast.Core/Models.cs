// Models.cs
using System;
using System.Collections.Generic;
namespace StuiPodcast.Core;

[Flags]
public enum PlayerCapabilities {
    None     = 0,
    Play     = 1 << 0,
    Pause    = 1 << 1,
    Stop     = 1 << 2,
    Seek     = 1 << 3,     // präzises Seek während der Wiedergabe
    Volume   = 1 << 4,     // Lautstärke live veränderbar
    Speed    = 1 << 5,     // Wiedergabegeschwindigkeit live veränderbar
    Network  = 1 << 6,     // Remote-URLs unterstützt
    Local    = 1 << 7      // lokale Dateien unterstützt
}

public class Feed {
    public Guid   Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset? LastChecked { get; set; }
}

public class Episode {
    public bool Saved { get; set; } = false;
    public bool Downloaded { get; set; } = false;
    public long? LastPosMs { get; set; }
    public long? LengthMs  { get; set; }
    public bool  Played    { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }

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

    // Fähigkeiten der aktiven Engine
    public PlayerCapabilities Capabilities { get; set; } =
        PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
        PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
        PlayerCapabilities.Network | PlayerCapabilities.Local;
}

// Netzprofil für Start-/Buffer-Verhalten (Engines lesen dies perspektivisch aus)
public enum NetworkProfile {
    Standard = 0,
    BadNetwork = 1
}

public class AppData {
    public string PlaySource { get; set; } = "auto";
    public bool   NetworkOnline { get; set; } = true;

    public List<Guid> Queue { get; set; } = new();
    public string? ThemePref { get; set; } // "Base", "MenuAccent", "Native" (Enum-Name)


    public string? DownloadDir { get; set; }
    public List<Guid> DownloadQueue { get; set; } = new();
    public Dictionary<Guid, StuiPodcast.Core.DownloadStatus> DownloadMap { get; set; } = new();

    public int HistorySize { get; set; } = 200;
    public string SortBy  { get; set; } = "pubdate";
    public string SortDir { get; set; } = "desc";

    public bool AutoAdvance  { get; set; } = true;
    public bool WrapAdvance  { get; set; } = true;
    public int  PlayedThresholdPercent { get; set; } = 95;

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
    
    public string PreferredEngine { get; set; } = "auto"; // auto|vlc|mpv|ffplay
    public string? LastEngineUsed { get; set; }           // Diagnose/OSD

    // NEU: Netzprofil (wirkt sich auf Engine-Startparameter aus, sobald Engines es auswerten)
    public NetworkProfile NetProfile { get; set; } = NetworkProfile.Standard;
}
