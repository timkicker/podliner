using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StuiPodcast.Core
{
    public class AppData
    {
        public string PlaySource { get; set; } = "auto";
        public bool NetworkOnline { get; set; } = true;

        public List<Guid> Queue { get; set; } = new();
        public string? ThemePref { get; set; } // "Base", "MenuAccent", "Native" (Enum-Name)


        public string? DownloadDir { get; set; }
        public List<Guid> DownloadQueue { get; set; } = new();
        public Dictionary<Guid, StuiPodcast.Core.DownloadStatus> DownloadMap { get; set; } = new();

        public int HistorySize { get; set; } = 200;
        public string SortBy { get; set; } = "pubdate";
        public string SortDir { get; set; } = "desc";

        public bool AutoAdvance { get; set; } = true;
        public bool WrapAdvance { get; set; } = true;
        public int PlayedThresholdPercent { get; set; } = 95;

        public bool UnplayedOnly { get; set; } = false;
        public Dictionary<Guid, int> LastSelectedEpisodeIndexByFeed { get; set; } = new();
        public int Volume0_100 { get; set; } = 50;
        public double Speed { get; set; } = 1.0;
        public int? LastVolume0_100 { get; set; }
        public double? LastSpeed { get; set; }

        public Guid? LastSelectedFeedId { get; set; }
        public int? LastSelectedEpisodeIndex { get; set; }

        public bool PlayerAtTop { get; set; } = false;
        public List<Feed> Feeds { get; set; } = new();
        public List<Episode> Episodes { get; set; } = new();

        public string PreferredEngine { get; set; } = "auto"; // auto|vlc|mpv|ffplay
        public string? LastEngineUsed { get; set; }           // Diagnose/OSD

        // NEU: Netzprofil (wirkt sich auf Engine-Startparameter aus, sobald Engines es auswerten)
        public NetworkProfile NetProfile { get; set; } = NetworkProfile.Standard;
    }
}
