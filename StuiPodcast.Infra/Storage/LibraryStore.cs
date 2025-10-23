using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    /// <summary>
    /// Persistenz für die Bibliothek (Feeds, Episoden, Queue, History) unter library/library.json
    /// mit atomarem Write (.tmp → replace), Debounce-Saves und Grundvalidierung.
    /// 
    /// WICHTIG:
    /// - KEINE Download-Felder in der Library.
    /// - Call-Site (z. B. FeedService) sollte URLs bereits kanonisieren.
    /// </summary>
    public sealed class LibraryStore
    {
        // Pfade
        public string ConfigDirectory { get; }
        public string LibraryDirectory { get; }
        public string FilePath { get; }
        public string TmpPath  { get; }

        // Status
        public bool   IsReadOnly      => _readOnly;
        public string? ReadOnlyReason => _readOnlyReason;

        // Aktueller Stand (In-Memory)
        public Library Current { get; private set; } = new();

        // Indizes (In-Memory, nicht persistiert)
        readonly Dictionary<Guid, Feed>    _feedsById    = new();
        readonly Dictionary<Guid, Episode> _episodesById = new();

        public event Action? Changed;

        // JSON-Optionen
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

        // Save-Steuerung
        readonly object _gate = new();
        Timer? _debounceTimer;
        TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(2500);
        volatile bool _savePending;
        volatile bool _isWriting;
        volatile bool _readOnly;
        volatile string? _readOnlyReason;

        public LibraryStore(string configDirectory, string? subFolder = "library", string fileName = "library.json")
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory must be provided", nameof(configDirectory));

            ConfigDirectory  = configDirectory;

            // WICHTIG: wenn subFolder leer/null → direkt ins ConfigDirectory schreiben
            LibraryDirectory = string.IsNullOrWhiteSpace(subFolder)
                ? ConfigDirectory
                : Path.Combine(ConfigDirectory, subFolder);

            FilePath = Path.Combine(LibraryDirectory, fileName);
            TmpPath  = FilePath + ".tmp";
        }


        /// <summary>
        /// Lädt oder erstellt eine leere Library. Ignoriert/entsorgt evtl. .tmp.
        /// Führt Grundvalidierung/Normalisierung durch und baut Indizes.
        /// </summary>
        public Library Load()
        {
            Directory.CreateDirectory(LibraryDirectory);
            TryDeleteTmp();

            Library lib;
            if (!File.Exists(FilePath))
            {
                lib = new Library();
            }
            else
            {
                try
                {
                    using var fs = File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    lib = JsonSerializer.Deserialize<Library>(fs, _readOptions) ?? new Library();
                }
                catch
                {
                    // Korrupt → leere Library
                    lib = new Library();
                }
            }

            ValidateAndNormalize(lib);
            Current = lib;
            RebuildIndices();
            return lib;
        }

        /// <summary>
        /// Debounced Save (Batch). Mehrere Mutationen in kurzem Abstand → ein Write.
        /// </summary>
        public void SaveAsync()
        {
            if (_readOnly) return;

            lock (_gate)
            {
                _savePending = true;
                _debounceTimer ??= new Timer(static s =>
                {
                    var self = (LibraryStore)s!;
                    self.TryPerformDebouncedSave();
                }, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Sofortiges Speichern (für :w / Abschluss). Überspringt Debounce.
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
                // Logging dem Caller überlassen
            }
        }

        // ---------------------------
        // Mutations-API (Convenience)
        // ---------------------------

        public Feed AddOrUpdateFeed(Feed feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            if (feed.Id == Guid.Empty) feed.Id = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(feed.Title)) feed.Title = string.Empty;
            feed.Url ??= string.Empty;

            // Upsert
            if (_feedsById.TryGetValue(feed.Id, out var existing))
            {
                existing.Title = feed.Title;
                existing.Url = feed.Url;
                existing.LastChecked = feed.LastChecked;
            }
            else
            {
                Current.Feeds.Add(feed);
                _feedsById[feed.Id] = feed;
            }

            SaveAsync();
            return _feedsById[feed.Id];
        }

        public Episode AddOrUpdateEpisode(Episode ep)
        {
            if (ep == null) throw new ArgumentNullException(nameof(ep));
            if (ep.Id == Guid.Empty) ep.Id = Guid.NewGuid();
            if (!_feedsById.ContainsKey(ep.FeedId))
                throw new InvalidOperationException("Episode.FeedId muss auf existierenden Feed zeigen.");
            if (ep.DurationMs < 0) ep.DurationMs = 0;
            ep.AudioUrl ??= string.Empty;
            ep.Title ??= string.Empty;
            ep.DescriptionText ??= string.Empty;
            ep.RssGuid = string.IsNullOrWhiteSpace(ep.RssGuid) ? null : ep.RssGuid;

            ep.Progress ??= new EpisodeProgress();
            if (ep.Progress.LastPosMs < 0) ep.Progress.LastPosMs = 0;
            if (ep.Progress.LastPosMs > ep.DurationMs && ep.DurationMs > 0)
                ep.Progress.LastPosMs = ep.DurationMs;

            if (_episodesById.TryGetValue(ep.Id, out var existing))
            {
                // Nur Metadaten updaten; Nutzungsstatus bleibt erhalten
                existing.Title = ep.Title;
                existing.AudioUrl = ep.AudioUrl;
                existing.RssGuid = ep.RssGuid;
                existing.PubDate = ep.PubDate;
                existing.DurationMs = ep.DurationMs;
                existing.DescriptionText = ep.DescriptionText;
                // Saved/Progress/ManuallyMarkedPlayed bewusst nicht überschreiben
            }
            else
            {
                Current.Episodes.Add(ep);
                _episodesById[ep.Id] = ep;
            }

            SaveAsync();
            return _episodesById[ep.Id];
        }

        public bool TryGetEpisode(Guid episodeId, out Episode? ep)
        {
            var ok = _episodesById.TryGetValue(episodeId, out var e);
            ep = e;
            return ok;
        }

        public Episode GetEpisodeOrThrow(Guid episodeId)
        {
            if (!_episodesById.TryGetValue(episodeId, out var e))
                throw new KeyNotFoundException($"Episode {episodeId} nicht gefunden.");
            return e;
        }

        public void SetEpisodeProgress(Guid episodeId, long lastPosMs, DateTimeOffset? lastPlayedAt)
        {
            var e = GetEpisodeOrThrow(episodeId);
            if (lastPosMs < 0) lastPosMs = 0;
            var effLen = Math.Max(e.DurationMs, 0);
            if (effLen > 0 && lastPosMs > effLen) lastPosMs = effLen;

            e.Progress ??= new EpisodeProgress();
            e.Progress.LastPosMs = lastPosMs;
            e.Progress.LastPlayedAt = lastPlayedAt;

            SaveAsync();
        }

        public void SetSaved(Guid episodeId, bool saved)
        {
            var e = GetEpisodeOrThrow(episodeId);
            e.Saved = saved;
            SaveAsync();
        }

        // Queue-Operationen
        public void QueuePush(Guid episodeId)
        {
            if (!_episodesById.ContainsKey(episodeId))
                throw new KeyNotFoundException($"Episode {episodeId} nicht gefunden.");

            Current.Queue.Add(episodeId);
            SaveAsync();
        }

        public bool QueueRemove(Guid episodeId)
        {
            var removed = Current.Queue.Remove(episodeId);
            if (removed) SaveAsync();
            return removed;
        }

        /// <summary>
        /// Entfernt alle Einträge in der Queue bis einschließlich episodeId (typisch: „bis aktuelles“).
        /// Wenn episodeId nicht gefunden wird, wird nichts entfernt.
        /// </summary>
        public void QueueTrimBefore(Guid episodeId)
        {
            var idx = Current.Queue.IndexOf(episodeId);
            if (idx >= 0)
            {
                Current.Queue.RemoveRange(0, idx + 1);
                SaveAsync();
            }
        }

        public void QueueClear()
        {
            if (Current.Queue.Count == 0) return;
            Current.Queue.Clear();
            SaveAsync();
        }

        // History
        public void HistoryAdd(Guid episodeId, DateTimeOffset atUtc)
        {
            if (!_episodesById.ContainsKey(episodeId))
                throw new KeyNotFoundException($"Episode {episodeId} nicht gefunden.");

            Current.History.Add(new HistoryItem { EpisodeId = episodeId, At = atUtc });
            SaveAsync();
        }

        public void HistoryClear()
        {
            if (Current.History.Count == 0) return;
            Current.History.Clear();
            SaveAsync();
        }

        // ---------------------------
        // Interna
        // ---------------------------

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
                // Logging dem Caller überlassen
            }
            finally
            {
                lock (_gate) _isWriting = false;
            }
        }

        void WriteFileAtomic(Library lib)
        {
            Directory.CreateDirectory(LibraryDirectory);

            // (1) Serialisieren → .tmp
            using (var fs = File.Open(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = _writeOptions.WriteIndented });
                JsonSerializer.Serialize(writer, lib, _writeOptions);
                writer.Flush();
                try { fs.Flush(true); } catch { /* best effort */ }
            }

            // (2) Replace/Move → Ziel
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

            // (3) Cleanup
            try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch { /* best effort */ }
        }

        void MarkReadOnly(Exception ex)
        {
            _readOnly = true;
            _readOnlyReason = ex.GetType().Name + ": " + ex.Message;
        }

        void RebuildIndices()
        {
            _feedsById.Clear();
            foreach (var f in Current.Feeds)
                _feedsById[f.Id] = f;

            _episodesById.Clear();
            foreach (var e in Current.Episodes)
                _episodesById[e.Id] = e;
        }

        static void ValidateAndNormalize(Library lib)
        {
            if (lib == null)
            {
                lib = new Library();
                return;
            }

            lib.SchemaVersion = lib.SchemaVersion <= 0 ? 1 : lib.SchemaVersion;

            // Feeds deduplizieren & säubern
            var feedMap = new Dictionary<Guid, Feed>();
            foreach (var f in lib.Feeds ?? new List<Feed>())
            {
                if (f == null) continue;
                if (f.Id == Guid.Empty) f.Id = Guid.NewGuid();
                f.Title ??= string.Empty;
                f.Url ??= string.Empty;
                if (!feedMap.ContainsKey(f.Id))
                    feedMap.Add(f.Id, f);
            }
            lib.Feeds.Clear();
            lib.Feeds.AddRange(feedMap.Values);

            var validFeedIds = new HashSet<Guid>(lib.Feeds.Select(x => x.Id));

            // Episoden deduplizieren & säubern
            var episodeMap = new Dictionary<Guid, Episode>();
            foreach (var e in lib.Episodes ?? new List<Episode>())
            {
                if (e == null) continue;
                if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
                if (!validFeedIds.Contains(e.FeedId)) continue; // verwaiste Episode verwerfen

                e.Title ??= string.Empty;
                e.AudioUrl ??= string.Empty;
                e.DescriptionText ??= string.Empty;
                e.RssGuid = string.IsNullOrWhiteSpace(e.RssGuid) ? null : e.RssGuid;

                if (e.DurationMs < 0) e.DurationMs = 0;

                e.Progress ??= new EpisodeProgress();
                if (e.Progress.LastPosMs < 0) e.Progress.LastPosMs = 0;
                if (e.DurationMs > 0 && e.Progress.LastPosMs > e.DurationMs)
                    e.Progress.LastPosMs = e.DurationMs;

                if (!episodeMap.ContainsKey(e.Id))
                    episodeMap.Add(e.Id, e);
            }
            lib.Episodes.Clear();
            lib.Episodes.AddRange(episodeMap.Values);

            var validEpisodeIds = new HashSet<Guid>(lib.Episodes.Select(x => x.Id));

            // Queue & History bereinigen
            if (lib.Queue == null) lib.Queue = new List<Guid>();
            lib.Queue = lib.Queue.Where(validEpisodeIds.Contains).ToList();

            if (lib.History == null) lib.History = new List<HistoryItem>();
            lib.History = lib.History
                .Where(h => h != null && validEpisodeIds.Contains(h.EpisodeId))
                .Select(h =>
                {
                    h.At = h.At == default ? DateTimeOffset.UtcNow : h.At;
                    return h;
                })
                .ToList();
        }

        void TryDeleteTmp()
        {
            try { if (File.Exists(TmpPath)) File.Delete(TmpPath); }
            catch { /* best effort */ }
        }
    }
}
