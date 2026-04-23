using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    // Persists feeds, episodes, queue and history to library.json.
    // All persistence machinery now comes from JsonStore<Library>; this class
    // adds the mutation API (AddOrUpdateFeed/Episode, Queue*, History*,
    // Remove*) and keeps fast-lookup indices in sync with the persisted list.
    public sealed class LibraryStore : JsonStore<Library>
    {
        public string ConfigDirectory  { get; }
        public string LibraryDirectory { get; }

        // in-memory indices, not persisted
        readonly Dictionary<Guid, Feed>    _feedsById    = new();
        readonly Dictionary<Guid, Episode> _episodesById = new();

        public LibraryStore(string configDirectory, string? subFolder = "library", string fileName = "library.json")
            : base(BuildPath(configDirectory, subFolder, fileName), TimeSpan.FromMilliseconds(2500))
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory must be provided", nameof(configDirectory));

            ConfigDirectory  = configDirectory;
            LibraryDirectory = string.IsNullOrWhiteSpace(subFolder)
                ? ConfigDirectory
                : Path.Combine(ConfigDirectory, subFolder);
        }

        static string BuildPath(string configDirectory, string? subFolder, string fileName)
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory must be provided", nameof(configDirectory));
            var dir = string.IsNullOrWhiteSpace(subFolder)
                ? configDirectory
                : Path.Combine(configDirectory, subFolder);
            return Path.Combine(dir, fileName);
        }

        protected override Library CreateDefault() => new();

        protected override void ValidateAndNormalize(Library lib)
        {
            lib.SchemaVersion = lib.SchemaVersion <= 0 ? 1 : lib.SchemaVersion;

            // feed cleanup and dedupe
            //   (a) drop rows whose URL isn't a well-formed http(s) URI — we
            //       observed "<feed>" (literal) sneaking in when users typed
            //       the placeholder in :add. Such rows fail every refresh.
            //   (b) dedup by Id first, then by URL. URL-duplicates produce a
            //       redirect table so episodes/queue/history can be rewritten
            //       to the surviving feed instead of orphaning them.
            var feedsById = new Dictionary<Guid, Feed>();
            foreach (var f in lib.Feeds)
            {
                if (f.Id == Guid.Empty) f.Id = Guid.NewGuid();
                if (!IsValidFeedUrl(f.Url)) continue;
                if (!feedsById.ContainsKey(f.Id))
                    feedsById.Add(f.Id, f);
            }

            // URL dedup (brownfield heal for the pre-URL-dedup AddOrUpdateFeed
            // bug: same URL was added N times as N distinct Ids).
            var feedRedirect = new Dictionary<Guid, Guid>();
            var byUrl = new Dictionary<string, Feed>(StringComparer.OrdinalIgnoreCase);
            var feedsKept = new List<Feed>();
            foreach (var f in feedsById.Values)
            {
                if (byUrl.TryGetValue(f.Url, out var winner))
                {
                    feedRedirect[f.Id] = winner.Id;
                    // Carry over richer metadata from the loser if the
                    // winner was less-populated (e.g. never refreshed).
                    if (string.IsNullOrWhiteSpace(winner.Title) && !string.IsNullOrWhiteSpace(f.Title))
                        winner.Title = f.Title;
                    if (!winner.LastChecked.HasValue || (f.LastChecked.HasValue && f.LastChecked > winner.LastChecked))
                        winner.LastChecked = f.LastChecked;
                    if (string.IsNullOrWhiteSpace(winner.LastEtag) && !string.IsNullOrWhiteSpace(f.LastEtag))
                        winner.LastEtag = f.LastEtag;
                    if (string.IsNullOrWhiteSpace(winner.LastModified) && !string.IsNullOrWhiteSpace(f.LastModified))
                        winner.LastModified = f.LastModified;
                }
                else
                {
                    byUrl[f.Url] = f;
                    feedsKept.Add(f);
                }
            }

            lib.Feeds.Clear();
            lib.Feeds.AddRange(feedsKept);

            if (feedRedirect.Count > 0)
            {
                // Episodes linked to a collapsed feed now point at the winner.
                foreach (var e in lib.Episodes)
                    if (feedRedirect.TryGetValue(e.FeedId, out var to))
                        e.FeedId = to;
            }

            var validFeedIds = new HashSet<Guid>(lib.Feeds.Select(x => x.Id));

            // episode cleanup and dedupe
            var episodeMap = new Dictionary<Guid, Episode>();
            foreach (var e in lib.Episodes)
            {
                if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
                if (!validFeedIds.Contains(e.FeedId)) continue;

                e.RssGuid = string.IsNullOrWhiteSpace(e.RssGuid) ? null : e.RssGuid;

                if (e.DurationMs < 0) e.DurationMs = 0;

                if (e.Progress.LastPosMs < 0) e.Progress.LastPosMs = 0;
                if (e.DurationMs > 0 && e.Progress.LastPosMs > e.DurationMs)
                    e.Progress.LastPosMs = e.DurationMs;

                if (!episodeMap.ContainsKey(e.Id))
                    episodeMap.Add(e.Id, e);
            }
            lib.Episodes.Clear();
            lib.Episodes.AddRange(episodeMap.Values);

            // Brownfield heal: merge duplicate episodes in the same feed that
            // share (Title, PubDate). Arises when a publisher migrates CDN
            // hosts — the feed refresh that shipped guid persistence
            // inserted fresh rows with guid+new-URL instead of matching the
            // legacy guid-less rows. Running here (on every load) means
            // upgraded users see a clean library on first start even if
            // they never run :refresh. loser→winner id rewrite keeps
            // queue/history references valid.
            var redirect = CollapseTitlePubDateDuplicates(lib);
            if (redirect.Count > 0)
            {
                lib.Queue   = lib.Queue.Select(id => redirect.GetValueOrDefault(id, id)).Distinct().ToList();
                lib.History = lib.History
                    .Select(h => { h.EpisodeId = redirect.GetValueOrDefault(h.EpisodeId, h.EpisodeId); return h; })
                    .ToList();
            }

            var validEpisodeIds = new HashSet<Guid>(lib.Episodes.Select(x => x.Id));

            // Dedup + drop stale ids. Duplicates can sneak in from legacy
            // saves (before Queue.Append's Contains-check) or via
            // MoveToFront/Move which only touch the first occurrence.
            lib.Queue = lib.Queue.Where(validEpisodeIds.Contains).Distinct().ToList();

            lib.History = lib.History
                .Where(h => validEpisodeIds.Contains(h.EpisodeId))
                .Select(h =>
                {
                    h.At = h.At == default ? DateTimeOffset.UtcNow : h.At;
                    return h;
                })
                .ToList();

            RebuildIndicesFrom(lib);
        }

        // Returns a map loser-id → winner-id for every removed duplicate so
        // the caller can rewrite queue/history references. Winner selection
        // preserves whichever row the user has interacted with (progress,
        // saved, manually played) — that Id is most likely already in the
        // queue/history. URL + RssGuid on the winner are replaced with the
        // freshest duplicate's values so the kept row doesn't stay stuck
        // on a stale CDN host.
        static Dictionary<Guid, Guid> CollapseTitlePubDateDuplicates(Library lib)
        {
            var redirect = new Dictionary<Guid, Guid>();

            // Normalize the title before grouping: some RSS parsers preserve
            // trailing whitespace (observed in the wild with pre-guid svmaudio
            // entries vs post-guid audiorella entries for the same episode),
            // which would otherwise keep the duplicates apart.
            var groups = lib.Episodes
                .Where(e => !string.IsNullOrWhiteSpace(e.Title) && e.PubDate.HasValue)
                .GroupBy(e => (e.FeedId, Title: e.Title!.Trim(), e.PubDate))
                .Where(g => g.Count() > 1)
                .ToList();

            if (groups.Count == 0) return redirect;

            var toRemove = new HashSet<Guid>();
            foreach (var group in groups)
            {
                var dupes = group.ToList();
                var winner = PickMergeWinner(dupes);
                var losers = dupes.Where(e => e.Id != winner.Id).ToList();

                foreach (var loser in losers)
                {
                    if (loser.Saved) winner.Saved = true;
                    if (loser.ManuallyMarkedPlayed) winner.ManuallyMarkedPlayed = true;
                    if (loser.Progress != null)
                    {
                        winner.Progress ??= new EpisodeProgress();
                        if (loser.Progress.LastPosMs > winner.Progress.LastPosMs)
                            winner.Progress.LastPosMs = loser.Progress.LastPosMs;
                        if (loser.Progress.LastPlayedAt is { } lp &&
                            (winner.Progress.LastPlayedAt is not { } wp || lp > wp))
                            winner.Progress.LastPlayedAt = lp;
                    }
                }

                // Adopt freshest URL+Guid: the duplicate with a non-null
                // RssGuid is by construction the one ingested by the
                // post-guid refresh, so its AudioUrl is current. Prefer it
                // over the winner's stale values even if the winner was
                // chosen for user state.
                var freshest = dupes.FirstOrDefault(e => !string.IsNullOrEmpty(e.RssGuid));
                if (freshest != null && freshest.Id != winner.Id)
                {
                    if (!string.IsNullOrEmpty(freshest.AudioUrl)) winner.AudioUrl = freshest.AudioUrl;
                    if (!string.IsNullOrEmpty(freshest.RssGuid))  winner.RssGuid  = freshest.RssGuid;
                }

                foreach (var loser in losers)
                {
                    redirect[loser.Id] = winner.Id;
                    toRemove.Add(loser.Id);
                }
            }

            if (toRemove.Count > 0)
                lib.Episodes.RemoveAll(e => toRemove.Contains(e.Id));

            return redirect;
        }

        // A feed URL is "valid" if it parses as an absolute http or https
        // URI. Anything else (empty, "<feed>", file:// paths) is noise and
        // gets dropped at load so it doesn't waste refresh cycles.
        static bool IsValidFeedUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        static Episode PickMergeWinner(List<Episode> dupes)
        {
            static bool HasState(Episode e)
                => e.Saved
                   || e.ManuallyMarkedPlayed
                   || e.Progress?.LastPlayedAt is not null
                   || (e.Progress?.LastPosMs ?? 0) > 0;

            var withState = dupes.Where(HasState).ToList();
            if (withState.Count > 0)
            {
                return withState
                    .OrderByDescending(e => e.Progress?.LastPlayedAt ?? DateTimeOffset.MinValue)
                    .ThenBy(e => e.Id)
                    .First();
            }

            var withGuid = dupes.Where(e => !string.IsNullOrEmpty(e.RssGuid)).ToList();
            if (withGuid.Count > 0) return withGuid.OrderBy(e => e.Id).First();

            return dupes.OrderBy(e => e.Id).First();
        }

        void RebuildIndicesFrom(Library lib)
        {
            _feedsById.Clear();
            foreach (var f in lib.Feeds) _feedsById[f.Id] = f;

            _episodesById.Clear();
            foreach (var e in lib.Episodes) _episodesById[e.Id] = e;
        }

        // ── feed upsert ─────────────────────────────────────────────────────
        public Feed AddOrUpdateFeed(Feed feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            if (string.IsNullOrWhiteSpace(feed.Title)) feed.Title = string.Empty;

            // URL-based dedup. Callers that run AddFeedAsync construct a new
            // probe Feed with a fresh Guid every time; without this check,
            // ":add <url>" twice creates two rows + double episodes instead
            // of a no-op. Also covers the legacy case where a Feed.Id was
            // never persisted (Guid.Empty).
            if (!string.IsNullOrWhiteSpace(feed.Url))
            {
                var dup = Current.Feeds.FirstOrDefault(f =>
                    string.Equals(f.Url, feed.Url, StringComparison.OrdinalIgnoreCase) &&
                    f.Id != feed.Id);
                if (dup != null)
                {
                    if (!string.IsNullOrWhiteSpace(feed.Title)) dup.Title = feed.Title;
                    if (feed.LastChecked.HasValue)              dup.LastChecked = feed.LastChecked;
                    SaveAsync();
                    return dup;
                }
            }

            if (feed.Id == Guid.Empty) feed.Id = Guid.NewGuid();

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

        // ── episode upsert, preserves usage flags ───────────────────────────
        public Episode AddOrUpdateEpisode(Episode ep)
        {
            if (ep == null) throw new ArgumentNullException(nameof(ep));
            if (ep.Id == Guid.Empty) ep.Id = Guid.NewGuid();
            if (!_feedsById.ContainsKey(ep.FeedId))
                throw new InvalidOperationException("Episode.FeedId muss auf existierenden Feed zeigen.");
            if (ep.DurationMs < 0) ep.DurationMs = 0;
            ep.RssGuid = string.IsNullOrWhiteSpace(ep.RssGuid) ? null : ep.RssGuid;

            if (ep.Progress.LastPosMs < 0) ep.Progress.LastPosMs = 0;
            if (ep.Progress.LastPosMs > ep.DurationMs && ep.DurationMs > 0)
                ep.Progress.LastPosMs = ep.DurationMs;

            if (_episodesById.TryGetValue(ep.Id, out var existing))
            {
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

        // ── queue operations ────────────────────────────────────────────────
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

        // ── history operations ──────────────────────────────────────────────
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

        // ── removals ────────────────────────────────────────────────────────
        public bool RemoveFeed(Guid feedId)
        {
            var removed = Current.Feeds.RemoveAll(f => f.Id == feedId) > 0;
            if (removed)
            {
                _feedsById.Remove(feedId);
                InvokeChangedAndSave();
            }
            return removed;
        }

        public int RemoveEpisodesByFeed(Guid feedId)
        {
            var toRemove = Current.Episodes.Where(e => e.FeedId == feedId).Select(e => e.Id).ToList();
            var cnt = Current.Episodes.RemoveAll(e => e.FeedId == feedId);
            if (cnt > 0)
            {
                foreach (var id in toRemove) _episodesById.Remove(id);
                InvokeChangedAndSave();
            }
            return cnt;
        }

        // Single-episode removal that keeps the internal index + list in sync
        // and persists. Symmetric counterpart to AddOrUpdateEpisode.
        public bool RemoveEpisode(Guid episodeId)
        {
            var removed = Current.Episodes.RemoveAll(e => e.Id == episodeId) > 0;
            if (removed)
            {
                _episodesById.Remove(episodeId);
                InvokeChangedAndSave();
            }
            return removed;
        }

        public int QueueRemoveByEpisodeIds(IEnumerable<Guid> episodeIds)
        {
            var set = episodeIds is HashSet<Guid> h ? h : new HashSet<Guid>(episodeIds);
            var before = Current.Queue.Count;
            Current.Queue.RemoveAll(id => set.Contains(id));
            var removed = before - Current.Queue.Count;
            if (removed > 0) InvokeChangedAndSave();
            return removed;
        }

        void InvokeChangedAndSave()
        {
            // Fire Changed manually for removals because the base class only
            // raises Changed after a successful write, but callers want an
            // immediate notification that the in-memory state changed.
            // The subsequent debounced save will run Changed a second time;
            // subscribers that care must be idempotent.
            SaveAsync();
        }
    }
}
