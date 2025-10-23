using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    /// <summary>
    /// Persistenz für App-/UI-Preferences (appsettings.json) mit atomarem Write:
    /// 1) .tmp schreiben + flush
    /// 2) Replace/Move -> Ziel
    /// 3) .tmp entfernen
    ///
    /// - Debounced SaveAsync (~1s)
    /// - SaveNow() für :w / Shutdown
    /// - Tolerantes Load (Kommentare, trailing commas)
    /// - ReadOnly-Erkennung (keine Schreibrechte)
    /// </summary>
    public sealed class ConfigStore
    {
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

        public ConfigStore(string configDirectory, string fileName = "appsettings.json")
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory must be provided", nameof(configDirectory));

            ConfigDirectory = configDirectory;
            FilePath = Path.Combine(ConfigDirectory, fileName);
            TmpPath  = FilePath + ".tmp";
        }

        /// <summary>
        /// Lädt Config oder erzeugt Defaults. Ignoriert/entsorgt evtl. .tmp.
        /// </summary>
        public AppConfig Load()
        {
            Directory.CreateDirectory(ConfigDirectory);

            // Verwaiste .tmp vom vorherigen Crash entsorgen (nicht laden).
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
                    // Korrupt → Defaults
                    cfg = new AppConfig();
                }
            }

            ValidateAndClamp(cfg);
            Current = cfg;
            return cfg;
        }

        /// <summary>
        /// Debounced Save. Mehrere Aufrufe innerhalb des Fensters führen zu einem Write.
        /// </summary>
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

        /// <summary>
        /// Sofortiger Save (z. B. für :w / Shutdown). Überspringt Debounce.
        /// </summary>
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
                // Logging der Caller-Seite überlassen
            }
        }

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
                // Logging der Caller-Seite überlassen
            }
            finally
            {
                lock (_gate) _isWriting = false;
            }
        }

        void WriteFileAtomic(AppConfig cfg)
        {
            Directory.CreateDirectory(ConfigDirectory);

            // (1) Serialisieren in .tmp
            using (var fs = File.Open(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = _writeOptions.WriteIndented });
                JsonSerializer.Serialize(writer, cfg, _writeOptions);
                writer.Flush();
                try { fs.Flush(true); } catch { /* Flush-to-disk best effort */ }
            }

            // (2) Replace/Move → Ziel
            if (File.Exists(FilePath))
            {
                // Prefer Replace (atomar, wenn unterstützt)
                try
                {
                    File.Replace(TmpPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    // Fallback: delete + move
                    File.Delete(FilePath);
                    File.Move(TmpPath, FilePath);
                }
            }
            else
            {
                File.Move(TmpPath, FilePath);
            }

            // (3) Cleanup (sollte nicht mehr existieren)
            try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch { /* best effort */ }
        }

        void MarkReadOnly(Exception ex)
        {
            _readOnly = true;
            _readOnlyReason = ex.GetType().Name + ": " + ex.Message;
        }

        /// <summary>
        /// Clamp + Normalisierung einfach halten, damit Persistenz robust bleibt.
        /// </summary>
        static void ValidateAndClamp(AppConfig c)
        {
            c.SchemaVersion = c.SchemaVersion <= 0 ? 1 : c.SchemaVersion;

            if (c.Volume0_100 < 0) c.Volume0_100 = 0;
            if (c.Volume0_100 > 100) c.Volume0_100 = 100;

            if (double.IsNaN(c.Speed) || c.Speed <= 0) c.Speed = 1.0;
            if (c.Speed < 0.25) c.Speed = 0.25;
            if (c.Speed > 4.0) c.Speed = 4.0;


            c.EnginePreference = NormalizeChoice(c.EnginePreference, "auto", "libvlc", "mpv", "ffplay");
            c.Theme = NormalizeChoice(c.Theme, "auto", "Base", "MenuAccent", "HighContrast", "Native");
            c.GlyphSet         = NormalizeChoice(c.GlyphSet, "auto", "unicode", "ascii");

            // View defaults
            c.ViewDefaults ??= new AppConfig.ViewDefaultsBlock();
            c.ViewDefaults.SortBy  = NormalizeChoice(c.ViewDefaults.SortBy, "pubdate", "title", "duration", "feed", "progress");
            c.ViewDefaults.SortDir = NormalizeChoice(c.ViewDefaults.SortDir, "asc", "desc");

            // Last selection
            c.LastSelection ??= new AppConfig.LastSelectionBlock();
            if (string.IsNullOrWhiteSpace(c.LastSelection.FeedId))
                c.LastSelection.FeedId = "virtual:all";
            c.LastSelection.Search ??= string.Empty;

            // UI block
            c.Ui ??= new AppConfig.UiBlock();
        }

        static string NormalizeChoice(string? value, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value)) return allowed[0];
            foreach (var a in allowed)
            {
                if (string.Equals(value, a, StringComparison.OrdinalIgnoreCase))
                    return a; // Rückgabe in kanonischer Schreibweise
            }
            return allowed[0];
        }
    }
}
