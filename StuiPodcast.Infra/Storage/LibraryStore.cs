using System.Text.Json;
using System.Text.Json.Serialization;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    // persistence for feeds, episodes, queue, history in library/library.json
    // atomic write via temp file and replace
    // debounced saves and basic validation
    // no download fields in the library
    // callers should canonicalize urls before storing
    public sealed class LibraryStore
    {
        #region fields and state

        // paths
        public string ConfigDirectory { get; }
        public string LibraryDirectory { get; }
        public string FilePath { get; }
        public string TmpPath  { get; }

        // current in memory snapshot
        public Library Current { get; private set; } = new();

        // in memory indices, not persisted
        readonly Dictionary<Guid, Feed>    _feedsById    = new();
        readonly Dictionary<Guid, Episode> _episodesById = new();

        public event Action? Changed;

        // json options
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

        // save coordination
        readonly object _gate = new();
        Timer? _debounceTimer;
        TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(2500);
        volatile bool _savePending;
        volatile bool _isWriting;
        volatile bool _readOnly;
        volatile string? _readOnlyReason;

        #endregion

        #region constructor

        public LibraryStore(string configDirectory, string? subFolder = "library", string fileName = "library.json")
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory must be provided", nameof(configDirectory));

            ConfigDirectory  = configDirectory;

            // if subFolder is empty or null then write directly into config directory
            LibraryDirectory = string.IsNullOrWhiteSpace(subFolder)
                ? ConfigDirectory
                : Path.Combine(ConfigDirectory, subFolder);

            FilePath = Path.Combine(LibraryDirectory, fileName);
            TmpPath  = FilePath + ".tmp";
        }

        #endregion

        #region load and save

        // load or create an empty library, remove stale temp, validate, build indices
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
                    // fall back to empty library on corrupt data
                    lib = new Library();
                }
            }

            ValidateAndNormalize(lib);
            Current = lib;
            RebuildIndices();
            return lib;
        }

        // debounced save, batches multiple mutations
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

        // immediate save, skips debounce
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

        #region mutations

        // feed upsert
        public Feed AddOrUpdateFeed(Feed feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            if (feed.Id == Guid.Empty) feed.Id = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(feed.Title)) feed.Title = string.Empty;
            feed.Url ??= string.Empty;

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

        // episode upsert, preserves usage flags
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
                // update metadata only
                existing.Title = ep.Title;
                existing.AudioUrl = ep.AudioUrl;
                existing.RssGuid = ep.RssGuid;
                existing.PubDate = ep.PubDate;
                existing.DurationMs = ep.DurationMs;
                existing.DescriptionText = ep.DescriptionText;
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

        // queue operations
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

        // removes entries up to and including the given episode id
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

        // history operations
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

        #endregion

        #region internals

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

        void WriteFileAtomic(Library lib)
        {
            Directory.CreateDirectory(LibraryDirectory);

            // serialize into temp and flush
            using (var fs = File.Open(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = _writeOptions.WriteIndented });
                JsonSerializer.Serialize(writer, lib, _writeOptions);
                writer.Flush();
                try { fs.Flush(true); } catch { /* best effort */ }
            }

            // replace target or move temp into place
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

            // cleanup temp
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

            // feed cleanup and dedupe
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

            // episode cleanup and dedupe
            var episodeMap = new Dictionary<Guid, Episode>();
            foreach (var e in lib.Episodes ?? new List<Episode>())
            {
                if (e == null) continue;
                if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
                if (!validFeedIds.Contains(e.FeedId)) continue;

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

            // queue and history cleanup
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

        #endregion
    }
}
