namespace StuiPodcast.Core
{
    // persisted app and ui preferences (appsettings.json)
    // no content and no downloads here
    public sealed class AppConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // playback and engine
        public string EnginePreference { get; set; } = "auto"; // auto, libvlc, mpv, ffplay
        public int    Volume0_100     { get; set; } = 65;      // range 0..100
        public double Speed           { get; set; } = 1.0;     // typically 0.5 to 3.0

        // ui theme and glyphs
        public string Theme    { get; set; } = "auto";         // auto, base, menuaccent, highcontrast
        public string GlyphSet { get; set; } = "auto";         // auto, unicode, ascii

        // network profile and offline startup
        public NetworkProfile NetworkProfile { get; set; } = NetworkProfile.Standard;
        public bool StartOffline { get; set; } = false;

        // ui layout
        public UiBlock Ui { get; set; } = new();

        // default view settings
        public ViewDefaultsBlock ViewDefaults { get; set; } = new();

        // last selected view and search
        public LastSelectionBlock LastSelection { get; set; } = new();

        public sealed class UiBlock
        {
            public bool PlayerAtTop { get; set; } = false;
            // room for more ui prefs
        }

        public sealed class ViewDefaultsBlock
        {
            public string SortBy       { get; set; } = "pubdate"; // pubdate, title, duration, feed, progress
            public string SortDir      { get; set; } = "desc";     // asc or desc
            public bool   UnplayedOnly { get; set; } = false;      // filter unplayed episodes
        }

        public sealed class LastSelectionBlock
        {
            // feed id (guid) or virtual feed like virtual:all, saved, downloaded, history, queue
            public string? FeedId { get; set; } = "virtual:all";
            public string? EpisodeId { get; set; } = null; // guid or null
            public string  Search { get; set; } = string.Empty;
        }
    }
}
