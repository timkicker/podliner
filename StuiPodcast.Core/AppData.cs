namespace StuiPodcast.Core
{
    public class AppData
    {
        public string PlaySource { get; set; } = "auto";
        public bool NetworkOnline { get; set; } = true;

        public string? ThemePref { get; set; } // "base", "menuaccent", "native" (enum name)

        public string? DownloadDir { get; set; }
        public List<Guid> DownloadQueue { get; set; } = new();
        public Dictionary<Guid, StuiPodcast.Core.DownloadStatus> DownloadMap { get; set; } = new();

        public int HistorySize { get; set; } = 200;
        public string? SortBy { get; set; } = "pubdate";
        public string? SortDir { get; set; } = "desc";
        public string? FeedSortBy  { get; set; } = "title";
        public string? FeedSortDir { get; set; } = "asc";

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

        // Audio engine preference (what the user asked for) and the engine
        // actually chosen on the last start (for the :engine diag OSD).
        // Persistence still round-trips through AppConfig.EnginePreference
        // as a string via AppBridge, so library.json format is stable.
        public AudioEngine PreferredEngine { get; set; } = AudioEngine.Auto;
        public AudioEngine? LastEngineUsed { get; set; }

        // network profile; affects engine startup params when engines use it
        public NetworkProfile NetProfile { get; set; } = NetworkProfile.Standard;
    }
}
