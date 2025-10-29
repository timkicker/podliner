using System.Text.Json;
using System.Text.Json.Serialization;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    // persistence for app and ui preferences in appsettings.json
    // atomic save via temp file, then replace target
    // debounced save helper and immediate save for explicit writes
    // tolerant load with comments and trailing commas
    // detects read only situations
    public sealed class ConfigStore
    {
        #region fields and state

        public string ConfigDirectory { get; }
        public string FilePath { get; }
        public string TmpPath  { get; }

        public bool   IsReadOnly      => _readOnly;
        public string? ReadOnlyReason => _readOnlyReason;

        public AppConfig Current { get; private set; } = new();

        public event Action? Changed;

        readonly JsonSerializerOptions _readOptions = new()
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true
        };

        readonly JsonSerializerOptions _writeOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        readonly object _gate = new();
        Timer? _debounceTimer;
        TimeSpan _debounceInterval = TimeSpan.FromSeconds(1);

        volatile bool _savePending;
        volatile bool _isWriting;
        volatile bool _readOnly;
        volatile string? _readOnlyReason;

        #endregion

        #region constructor

        public ConfigStore(string configDirectory, string fileName = "appsettings.json")
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory must be provided", nameof(configDirectory));

            ConfigDirectory = configDirectory;
            FilePath = Path.Combine(ConfigDirectory, fileName);
            TmpPath  = FilePath + ".tmp";
        }

        #endregion

        #region load and save api

        // load config or create defaults; removes any stale temp file
        public AppConfig Load()
        {
            Directory.CreateDirectory(ConfigDirectory);

            // remove orphaned temp file from a previous crash
            try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch { /* best effort */ }

            AppConfig cfg;
            if (!File.Exists(FilePath))
            {
                cfg = new AppConfig();
            }
            else
            {
                try
                {
                    using var fs = File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    cfg = JsonSerializer.Deserialize<AppConfig>(fs, _readOptions) ?? new AppConfig();
                }
                catch
                {
                    // fall back to defaults on corrupt data
                    cfg = new AppConfig();
                }
            }

            ValidateAndClamp(cfg);
            Current = cfg;
            return cfg;
        }

        // debounced save; multiple calls collapse into one write
        public void SaveAsync()
        {
            if (_readOnly) return;

            lock (_gate)
            {
                _savePending = true;
                _debounceTimer ??= new Timer(static s =>
                {
                    var self = (ConfigStore)s!;
                    self.TryPerformDebouncedSave();
                }, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            }
        }

        // immediate save for explicit write or shutdown
        public void SaveNow()
        {
            if (_readOnly) return;

            lock (_gate)
            {
                _savePending = false;
                _debounceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            try
            {
                WriteFileAtomic(Current);
                Changed?.Invoke();
            }
            catch (UnauthorizedAccessException ex)
            {
                MarkReadOnly(ex);
            }
            catch
            {
                // leave logging to the caller
            }
        }

        #endregion

        #region debounce worker

        void TryPerformDebouncedSave()
        {
            if (_readOnly) return;

            lock (_gate)
            {
                if (_isWriting || !_savePending) return;
                _isWriting = true;
                _savePending = false;
            }

            try
            {
                WriteFileAtomic(Current);
                Changed?.Invoke();
            }
            catch (UnauthorizedAccessException ex)
            {
                MarkReadOnly(ex);
            }
            catch
            {
                // leave logging to the caller
            }
            finally
            {
                lock (_gate) _isWriting = false;
            }
        }

        #endregion

        #region file io helpers

        void WriteFileAtomic(AppConfig cfg)
        {
            Directory.CreateDirectory(ConfigDirectory);

            // write json to temp file and flush
            using (var fs = File.Open(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = _writeOptions.WriteIndented });
                JsonSerializer.Serialize(writer, cfg, _writeOptions);
                writer.Flush();
                try { fs.Flush(true); } catch { /* flush to disk best effort */ }
            }

            // replace target if present, otherwise move temp into place
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Replace(TmpPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(FilePath);
                    File.Move(TmpPath, FilePath);
                }
            }
            else
            {
                File.Move(TmpPath, FilePath);
            }

            // cleanup temp file if it still exists
            try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch { /* best effort */ }
        }

        void MarkReadOnly(Exception ex)
        {
            _readOnly = true;
            _readOnlyReason = ex.GetType().Name + ": " + ex.Message;
        }

        #endregion

        #region validation

        // simple clamp and normalization to keep persistence robust
        static void ValidateAndClamp(AppConfig c)
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

            // view defaults
            c.ViewDefaults.SortBy  = NormalizeChoice(c.ViewDefaults.SortBy, "pubdate", "title", "duration", "feed", "progress");
            c.ViewDefaults.SortDir = NormalizeChoice(c.ViewDefaults.SortDir, "asc", "desc");

            // last selection
            if (string.IsNullOrWhiteSpace(c.LastSelection.FeedId))
                c.LastSelection.FeedId = "virtual:all";
            
        }

        static string NormalizeChoice(string? value, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value)) return allowed[0];
            foreach (var a in allowed)
            {
                if (string.Equals(value, a, StringComparison.OrdinalIgnoreCase))
                    return a; // return canonical casing
            }
            return allowed[0];
        }

        #endregion
    }
}
