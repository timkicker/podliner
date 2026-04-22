using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    // Persists app + UI preferences to appsettings.json.
    // All the persistence boilerplate (atomic writes, debounce, corrupt-file
    // fallback, read-only detection, dispose flushes pending) now lives in
    // JsonStore<T>. This class only defines the file path, default interval,
    // default instance and the validation/clamping rules.
    public sealed class ConfigStore : JsonStore<AppConfig>
    {
        public string ConfigDirectory { get; }

        public AppConfig Current1 => Current; // (alias-compat placeholder, unused)

        public ConfigStore(string configDirectory, string fileName = "appsettings.json")
            : base(Path.Combine(configDirectory, fileName), TimeSpan.FromSeconds(1))
        {
            ConfigDirectory = configDirectory;
        }

        protected override AppConfig CreateDefault() => new();

        protected override void ValidateAndNormalize(AppConfig c)
        {
            c.SchemaVersion = c.SchemaVersion <= 0 ? 1 : c.SchemaVersion;

            if (c.Volume0_100 < 0) c.Volume0_100 = 0;
            if (c.Volume0_100 > 100) c.Volume0_100 = 100;

            if (double.IsNaN(c.Speed) || c.Speed <= 0) c.Speed = 1.0;
            if (c.Speed < 0.25) c.Speed = 0.25;
            if (c.Speed > 4.0) c.Speed = 4.0;

            c.EnginePreference = NormalizeChoice(c.EnginePreference, "auto", "libvlc", "mpv", "ffplay");
            c.Theme            = NormalizeChoice(c.Theme, "auto", "Base", "MenuAccent", "HighContrast", "Native");
            c.GlyphSet         = NormalizeChoice(c.GlyphSet, "auto", "unicode", "ascii");

            c.ViewDefaults.SortBy  = NormalizeChoice(c.ViewDefaults.SortBy, "pubdate", "title", "duration", "feed", "progress");
            c.ViewDefaults.SortDir = NormalizeChoice(c.ViewDefaults.SortDir, "asc", "desc");

            if (string.IsNullOrWhiteSpace(c.LastSelection.FeedId))
                c.LastSelection.FeedId = "virtual:all";
        }

        static string NormalizeChoice(string? value, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value)) return allowed[0];
            foreach (var a in allowed)
                if (string.Equals(value, a, StringComparison.OrdinalIgnoreCase))
                    return a;
            return allowed[0];
        }
    }
}
