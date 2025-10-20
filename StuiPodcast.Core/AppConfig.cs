using System;

namespace StuiPodcast.Core
{
    /// <summary>
    /// Persistierte App-/UI-Preferences (appsettings.json).
    /// Keine Inhalte, keine Downloads.
    /// </summary>
    public sealed class AppConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Playback / Engine
        public string EnginePreference { get; set; } = "auto"; // "auto" | "libvlc" | "mpv" | "ffplay"
        public int    Volume0_100     { get; set; } = 65;      // 0..100
        public double Speed           { get; set; } = 1.0;     // typ. 0.5..3.0

        // UI / Theme / Glyphs
        public string Theme    { get; set; } = "auto";         // "Base" | "MenuAccent" | "HighContrast"
        public string GlyphSet { get; set; } = "auto";         // "auto" | "unicode" | "ascii"

        // Netzprofil & Offline-Start
        public NetworkProfile NetworkProfile { get; set; } = NetworkProfile.Standard;
        public bool StartOffline { get; set; } = false;

        // UI Layout
        public UiBlock Ui { get; set; } = new();

        // View-Defaults
        public ViewDefaultsBlock ViewDefaults { get; set; } = new();

        // Zuletzt gewählte Ansicht
        public LastSelectionBlock LastSelection { get; set; } = new();

        public sealed class UiBlock
        {
            public bool PlayerAtTop { get; set; } = false;
            // Platz für weitere UI-Prefs (Fenstergröße etc.)
        }

        public sealed class ViewDefaultsBlock
        {
            public string SortBy       { get; set; } = "pubdate"; // "pubdate" | "title" | "duration" | "feed" | "progress"
            public string SortDir      { get; set; } = "desc";     // "asc" | "desc"
            public bool   UnplayedOnly { get; set; } = false;
        }

        public sealed class LastSelectionBlock
        {
            /// <summary>Feed-ID (GUID) oder virtueller Feed ("virtual:all|saved|downloaded|history|queue")</summary>
            public string? FeedId { get; set; } = "virtual:all";
            public string? EpisodeId { get; set; } = null; // GUID oder null
            public string  Search { get; set; } = string.Empty;
        }
    }
}
