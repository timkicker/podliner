using CodeHollow.FeedReader;
using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Infra.Feeds;
using StuiPodcast.Infra.Storage;
using CoreFeed = StuiPodcast.Core.Feed;
using FeedItem = CodeHollow.FeedReader.FeedItem;
using RssFeed = CodeHollow.FeedReader.Feed;

namespace StuiPodcast.Infra
{
    // Orchestrates feed add / remove / refresh against the library. Three
    // concerns that each have their own unit:
    //
    //   FeedHttpFetcher — HTTP transport (User-Agent, decompression, timeout).
    //   RssParser       — extract fields (audio URL, guid, duration, date,
    //                     description text) from a CodeHollow FeedItem.
    //   FeedService     — sequencing + dedup + persistence (this class).
    //
    // The UI marshals mutations onto the main loop via _uiDispatch so
    // concurrent enumerations don't race with the refresh loop.
    public class FeedService : IDisposable
    {
        #region fields and ctor

        private readonly AppData _data;
        private readonly AppFacade _app;
        private readonly FeedHttpFetcher _fetcher;
        private readonly Func<Action, Task> _uiDispatch;

        // Reentrance guard so double-triggered :refresh (e.g. user mashes the
        // key) doesn't run two concurrent full sweeps that mutate the same
        // episodes dict in parallel.
        private int _refreshingAll;

        // Fires after a refresh adds new episodes. Listeners (e.g.
        // Program.cs wiring auto-download) receive the feed + new episode
        // ids so they can decide what to do. Errors in listeners are
        // swallowed so one bad subscriber can't break refreshes.
        public event Action<CoreFeed, IReadOnlyList<Guid>>? NewEpisodesDetected;

        // Fires when a single feed refresh fails. The reason is a short
        // human-friendly string ("HTTP 404", "timed out", "parse failed")
        // suitable for an OSD. Subscribers should throttle / aggregate.
        public event Action<CoreFeed, string>? FeedRefreshFailed;

        public FeedService(AppData data, AppFacade app, Func<Action, Task>? uiDispatch = null)
            : this(data, app, new FeedHttpFetcher(), uiDispatch) { }

        // For testing: inject a custom HttpMessageHandler (e.g., FakeHttpHandler).
        public FeedService(AppData data, AppFacade app, HttpMessageHandler handler, Func<Action, Task>? uiDispatch = null)
            : this(data, app, new FeedHttpFetcher(handler), uiDispatch) { }

        private FeedService(AppData data, AppFacade app, FeedHttpFetcher fetcher, Func<Action, Task>? uiDispatch)
        {
            _data = data;
            _app = app;
            _fetcher = fetcher;
            _uiDispatch = uiDispatch ?? (a => { a(); return Task.CompletedTask; });
        }

        #endregion

        #region public api

        public async Task RemoveFeedAsync(Guid feedId, bool removeDownloads = false)
        {
            HashSet<Guid> epIds = new();
            await _uiDispatch(() =>
            {
                epIds = _app.Episodes.Where(e => e.FeedId == feedId).Select(e => e.Id).ToHashSet();
            }).ConfigureAwait(false);

            var removedFeed = _app.RemoveFeed(feedId);
            var removedEps = _app.RemoveEpisodesByFeed(feedId);
            var removedFromQ = _app.QueueRemoveByEpisodeIds(epIds);

            await _uiDispatch(() =>
            {
                if (_data.LastSelectedFeedId == feedId) _data.LastSelectedFeedId = null;
            }).ConfigureAwait(false);

            if (removeDownloads)
            {
                foreach (var id in epIds)
                    if (_app.TryGetLocalPath(id, out var path) && !string.IsNullOrWhiteSpace(path))
                        try { System.IO.File.Delete(path!); } catch { /* best effort */ }
            }

            _app.SaveNow();

            Log.Information("feed/remove persisted id={FeedId} feedRemoved={Feed} episodesRemoved={Eps} queueRemoved={Q}",
                feedId, removedFeed, removedEps, removedFromQ);
        }

        public async Task<CoreFeed> AddFeedAsync(string url)
        {
            Log.Information("feed/add url={Url}", url);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                Log.Warning("feed/add rejected non-http url={Url}", url);
                throw new ArgumentException($"feed URL must be http(s): '{url}'", nameof(url));
            }

            // quick probe, best effort
            var probeFeed = new CoreFeed { Url = url, Title = url, LastChecked = DateTimeOffset.Now };
            try
            {
                var xml = await _fetcher.FetchXmlAsync(url).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(xml))
                {
                    var f = FeedReader.ReadFromString(xml);
                    Log.Information("feed/probe ok url={Url} title={Title} items={Count}", url, f.Title, f.Items?.Count ?? 0);
                    probeFeed.Title = string.IsNullOrWhiteSpace(f.Title) ? url : f.Title!;
                    probeFeed.LastChecked = DateTimeOffset.Now;
                }
                else
                {
                    Log.Warning("feed/probe empty response url={Url}", url);
                    probeFeed.LastChecked = DateTimeOffset.Now;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "feed/probe parse fail url={Url}", url);
                probeFeed.LastChecked = DateTimeOffset.Now;
            }

            // persistent upsert to get a stable id
            CoreFeed saved = null!;
            await _uiDispatch(() => saved = _app.AddOrUpdateFeed(probeFeed)).ConfigureAwait(false);

            // best effort first refresh
            try { await RefreshFeedAsync(saved).ConfigureAwait(false); }
            catch { /* ignored */ }

            return saved;
        }

        public async Task RefreshAllAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _refreshingAll, 1) == 1)
            {
                Log.Debug("feed/refresh-all already in progress — skipping");
                return;
            }

            try
            {
                // snapshot the feed list on the UI thread to avoid enumerating a live list
                List<CoreFeed> snapshot = new();
                await _uiDispatch(() => snapshot = _app.Feeds.ToList()).ConfigureAwait(false);

                // intentionally sequential
                foreach (var feed in snapshot)
                {
                    try { await RefreshFeedAsync(feed).ConfigureAwait(false); }
                    catch { /* best effort per feed */ }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _refreshingAll, 0);
            }
        }

        public async Task RefreshFeedAsync(CoreFeed feed)
        {
            RssFeed f;

            // Conditional GET: send back last-seen ETag + Last-Modified.
            // 304 = no change, skip parse entirely (still bumps LastChecked
            // so the user sees progress).
            var fetch = await _fetcher.FetchAsync(feed.Url, feed.LastEtag, feed.LastModified).ConfigureAwait(false);

            if (fetch.IsNotModified)
            {
                Log.Debug("feed/refresh 304 url={Url}", feed.Url);
                feed.LastChecked = DateTimeOffset.Now;
                await _uiDispatch(() => _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);
                return;
            }

            if (fetch.IsFailure)
            {
                var reason = FormatFailure(fetch);
                Log.Warning("feed/refresh failed url={Url} reason={Reason}", feed.Url, reason);
                feed.LastChecked = DateTimeOffset.Now;
                await _uiDispatch(() => _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);
                try { FeedRefreshFailed?.Invoke(feed, reason); }
                catch (Exception ex) { Log.Debug(ex, "feed/failure subscriber threw id={Id}", feed.Id); }
                return;
            }

            if (string.IsNullOrWhiteSpace(fetch.Xml))
            {
                // Last-chance branch — shouldn't fire once FetchResult always
                // carries a failure category, but keep a safety net.
                Log.Warning("feed/refresh empty body url={Url}", feed.Url);
                feed.LastChecked = DateTimeOffset.Now;
                await _uiDispatch(() => _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);
                return;
            }

            try
            {
                f = FeedReader.ReadFromString(fetch.Xml!);
                Log.Information("feed/refresh ok id={Id} title={Title} items={Items}",
                    feed.Id, f.Title ?? feed.Title, f.Items?.Count ?? 0);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "feed/refresh parse fail url={Url}", feed.Url);
                feed.LastChecked = DateTimeOffset.Now;
                await _uiDispatch(() => _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);
                try { FeedRefreshFailed?.Invoke(feed, "feed parse failed"); }
                catch (Exception ex2) { Log.Debug(ex2, "feed/failure subscriber threw id={Id}", feed.Id); }
                return;
            }

            // gently update and persist feed metadata
            if (string.IsNullOrWhiteSpace(feed.Title))
                feed.Title = f.Title ?? feed.Url;
            feed.LastChecked = DateTimeOffset.Now;
            // Store freshness hints only on a real 200 — 304 keeps the old
            // values by construction (falls through via early-return above).
            if (!string.IsNullOrWhiteSpace(fetch.Etag))         feed.LastEtag     = fetch.Etag;
            if (!string.IsNullOrWhiteSpace(fetch.LastModified)) feed.LastModified = fetch.LastModified;

            CoreFeed persistedFeed = null!;
            await _uiDispatch(() => persistedFeed = _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);

            int added = 0, updated = 0, skippedNoAudio = 0;
            var newEpisodeIds = new List<Guid>();
            var items = (f.Items ?? Array.Empty<FeedItem>()).ToList();

            // Parse + decide + mutate the library on the UI thread so concurrent UI
            // enumerations of the episode list don't trip over us. The heavy
            // (Title, PubDate) dedup pass lives in LibraryStore.ValidateAndNormalize
            // so it runs on every load, not just refresh.
            await _uiDispatch(() =>
            {
                // Build three O(1) lookups from the existing feed episodes:
                //   byGuid — primary dedup key; stable across CDN migrations
                //            (podcast publishers rotate CDN URLs but keep
                //             the RSS <guid> stable, so matching on guid
                //             lets us transparently update the AudioUrl).
                //   byUrl  — fallback for feeds that don't emit <guid> or
                //            for episodes we ingested before we started
                //            persisting RssGuid (older library.json files).
                //   byTitlePub — last-resort fallback: same title + pubdate.
                //            Catches brownfield CDN migrations on feeds we
                //            ingested before guid persistence, where URL
                //            and guid both differ from the stored entry.
                var byGuid = new Dictionary<string, Episode>(StringComparer.Ordinal);
                var byUrl  = new Dictionary<string, Episode>(StringComparer.OrdinalIgnoreCase);
                var byTitlePub = new Dictionary<(string, DateTimeOffset?), Episode>();
                foreach (var e in _app.Episodes)
                {
                    if (e.FeedId != persistedFeed.Id) continue;
                    if (!string.IsNullOrEmpty(e.RssGuid)) byGuid[e.RssGuid] = e;
                    if (!string.IsNullOrEmpty(e.AudioUrl)) byUrl[e.AudioUrl] = e;
                    if (!string.IsNullOrWhiteSpace(e.Title) && e.PubDate.HasValue)
                        byTitlePub[(e.Title!.Trim(), e.PubDate)] = e;
                }

                foreach (var item in items)
                {
                    var audioUrl = RssParser.TryGetAudioUrl(item);
                    if (string.IsNullOrWhiteSpace(audioUrl)) { skippedNoAudio++; continue; }

                    var guid  = RssParser.TryGetGuid(item);
                    var pub   = RssParser.ParseDate(item);
                    var lenMs = RssParser.TryGetDurationMs(item) ?? 0;
                    var desc  = RssParser.HtmlToText(item.Content ?? item.Description ?? "");
                    var title = item.Title ?? "(untitled)";
                    var chaptersUrl = RssParser.TryGetChaptersUrl(item);

                    Episode? existing = null;
                    if (!string.IsNullOrEmpty(guid)) byGuid.TryGetValue(guid, out existing);
                    if (existing == null) byUrl.TryGetValue(audioUrl, out existing);
                    if (existing == null && !string.IsNullOrWhiteSpace(title) && pub.HasValue)
                        byTitlePub.TryGetValue((title.Trim(), pub), out existing);

                    if (existing == null)
                    {
                        var ep = new Episode
                        {
                            Id = Guid.NewGuid(),
                            FeedId = persistedFeed.Id,
                            Title = title,
                            AudioUrl = audioUrl,
                            RssGuid = guid,
                            PubDate = pub,
                            DurationMs = lenMs,
                            DescriptionText = desc,
                            ChaptersUrl = chaptersUrl,
                            Saved = false,
                            Progress = new EpisodeProgress()
                        };

                        var persistedEp = _app.AddOrUpdateEpisode(ep);
                        if (!string.IsNullOrEmpty(guid)) byGuid[guid] = persistedEp;
                        byUrl[audioUrl] = persistedEp;
                        newEpisodeIds.Add(persistedEp.Id);
                        added++;
                    }
                    else
                    {
                        // Backfill guid once we see it — older library entries
                        // were created before guid persistence landed.
                        if (string.IsNullOrEmpty(existing.RssGuid) && !string.IsNullOrEmpty(guid))
                            existing.RssGuid = guid;

                        // CDN migration: feed now advertises a different URL
                        // for the same guid. Update our copy so playback stops
                        // hitting stale 404s. Also re-key the local lookup so
                        // later items in this loop find the new URL too.
                        if (!string.Equals(existing.AudioUrl, audioUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(existing.AudioUrl))
                                byUrl.Remove(existing.AudioUrl);
                            existing.AudioUrl = audioUrl;
                            byUrl[audioUrl] = existing;
                        }

                        if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(title))
                            existing.Title = title;
                        if (!existing.PubDate.HasValue && pub.HasValue)
                            existing.PubDate = pub;
                        if (string.IsNullOrWhiteSpace(existing.DescriptionText) && !string.IsNullOrWhiteSpace(desc))
                            existing.DescriptionText = desc;
                        if (existing.DurationMs <= 0 && lenMs > 0)
                            existing.DurationMs = lenMs;
                        // Update chapters URL if the feed now advertises one
                        // (or changed host). Clears the cached list so next
                        // :chapters load refetches.
                        if (!string.Equals(existing.ChaptersUrl, chaptersUrl, StringComparison.Ordinal))
                        {
                            existing.ChaptersUrl = chaptersUrl;
                            existing.Chapters = new();
                        }

                        var persistedEp = _app.AddOrUpdateEpisode(existing);
                        updated++;
                    }
                }
            }).ConfigureAwait(false);

            Log.Information("feed/refresh summary id={Id} added={Added} updated={Updated} skippedNoAudio={Skipped}",
                feed.Id, added, updated, skippedNoAudio);

            if (newEpisodeIds.Count > 0)
            {
                try { NewEpisodesDetected?.Invoke(persistedFeed, newEpisodeIds); }
                catch (Exception ex) { Log.Debug(ex, "feed/new-episodes subscriber threw id={Id}", persistedFeed.Id); }
            }
        }

        // Short, user-facing summary of a fetch failure. Used for OSDs.
        internal static string FormatFailure(FetchResult r) => r.Failure switch
        {
            FetchFailure.NotFound    => $"HTTP 404 (feed URL moved or removed)",
            FetchFailure.Forbidden   => $"HTTP {r.HttpStatus} (blocked — CDN or auth)",
            FetchFailure.ServerError => $"HTTP {r.HttpStatus} (server error — retry later)",
            FetchFailure.ClientError => $"HTTP {r.HttpStatus} {r.FailureDetail ?? ""}".TrimEnd(),
            FetchFailure.Timeout     => "timed out",
            FetchFailure.Unreachable => "unreachable (check network)",
            _                        => r.FailureDetail ?? "fetch failed"
        };

        #endregion

        #region dispose

        public void Dispose()
        {
            try { _fetcher.Dispose(); } catch { /* best effort */ }
        }

        #endregion
    }
}
