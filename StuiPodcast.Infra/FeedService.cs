using System.Globalization;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using CodeHollow.FeedReader;
using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using CoreFeed = StuiPodcast.Core.Feed;
using FeedItem = CodeHollow.FeedReader.FeedItem;
using RssFeed = CodeHollow.FeedReader.Feed;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace StuiPodcast.Infra
{
    public class FeedService : IDisposable
    {
        #region fields and ctor

        private readonly AppData _data;
        private readonly AppFacade _app;
        private readonly HttpClient _http;
        // Marshals LibraryStore mutations to the UI thread so concurrent UI reads
        // don't race our thread-pool continuations. Defaults to synchronous for tests.
        private readonly Func<Action, Task> _uiDispatch;

        public FeedService(AppData data, AppFacade app, Func<Action, Task>? uiDispatch = null)
            : this(data, app, BuildDefaultHandler(), uiDispatch) { }

        // For testing: inject a custom HttpMessageHandler (e.g., FakeHttpHandler).
        public FeedService(AppData data, AppFacade app, HttpMessageHandler handler, Func<Action, Task>? uiDispatch = null)
        {
            _data = data;
            _app = app;
            _uiDispatch = uiDispatch ?? (a => { a(); return Task.CompletedTask; });

            _http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.UserAgent.Clear();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("podliner/1.0.1 (+https://github.com/yourrepo)");
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        }

        private static HttpClientHandler BuildDefaultHandler() => new()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

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

            // quick probe, best effort
            var probeFeed = new CoreFeed { Url = url, Title = url, LastChecked = DateTimeOffset.Now };
            try
            {
                var xml = await FetchFeedXmlAsync(url).ConfigureAwait(false);
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

        public async Task RefreshFeedAsync(CoreFeed feed)
        {
            RssFeed f;

            // http fetch with ua
            var xml = await FetchFeedXmlAsync(feed.Url).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml))
            {
                // make it visible that the fetch failed (403/401 etc.)
                Log.Warning("feed/refresh no content url={Url}", feed.Url);
                feed.LastChecked = DateTimeOffset.Now;
                await _uiDispatch(() => _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);
                return;
            }


            try
            {
                f = FeedReader.ReadFromString(xml);
                Log.Information("feed/refresh ok id={Id} title={Title} items={Items}",
                    feed.Id, f.Title ?? feed.Title, f.Items?.Count ?? 0);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "feed/refresh parse fail url={Url}", feed.Url);
                feed.LastChecked = DateTimeOffset.Now;
                await _uiDispatch(() => _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);
                return;
            }

            // gently update and persist feed metadata
            if (string.IsNullOrWhiteSpace(feed.Title))
                feed.Title = f.Title ?? feed.Url;
            feed.LastChecked = DateTimeOffset.Now;

            CoreFeed persistedFeed = null!;
            await _uiDispatch(() => persistedFeed = _app.AddOrUpdateFeed(feed)).ConfigureAwait(false);

            int added = 0, updated = 0, skippedNoAudio = 0;
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
                    var audioUrl = TryGetAudioUrl(item);
                    if (string.IsNullOrWhiteSpace(audioUrl)) { skippedNoAudio++; continue; }

                    var guid  = TryGetGuid(item);
                    var pub   = ParseDate(item);
                    var lenMs = TryGetDurationMs(item) ?? 0;
                    var desc  = HtmlToText(item.Content ?? item.Description ?? "");
                    var title = item.Title ?? "(untitled)";

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
                            Saved = false,
                            Progress = new EpisodeProgress()
                        };

                        var persistedEp = _app.AddOrUpdateEpisode(ep);
                        if (!string.IsNullOrEmpty(guid)) byGuid[guid] = persistedEp;
                        byUrl[audioUrl] = persistedEp;
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

                        var persistedEp = _app.AddOrUpdateEpisode(existing);
                        updated++;
                    }
                }
            }).ConfigureAwait(false);

            Log.Information("feed/refresh summary id={Id} added={Added} updated={Updated} skippedNoAudio={Skipped}",
                feed.Id, added, updated, skippedNoAudio);
        }

        #endregion

        #region helpers

        static DateTimeOffset? ParseDate(FeedItem item)
        {
            if (item.PublishingDate.HasValue) return item.PublishingDate.Value;

            if (!string.IsNullOrWhiteSpace(item.PublishingDateString) &&
                DateTimeOffset.TryParse(item.PublishingDateString, out var d))
                return d;

            return null;
        }

        private async Task<string?> FetchFeedXmlAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    Log.Warning("feed/http status={Status} url={Url}", (int)resp.StatusCode, url);
                    return null;
                }

                // read string (parser handles encoding in xml)
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "feed/http fail url={Url}", url);
                return null;
            }
        }

        // RSS2 <guid> (or Atom <id>) — the stable episode identifier the
        // publisher promises not to rotate. Primary dedup key in the
        // refresh loop; lets us follow CDN migrations transparently.
        // isPermaLink="false" is common (publishers opt out of URI
        // semantics); we don't care either way — we just need the text.
        static string? TryGetGuid(FeedItem item)
        {
            var root = item.SpecificItem?.Element as XElement;
            if (root == null) return null;

            foreach (var node in root.Elements())
            {
                var ln = node.Name.LocalName.ToLowerInvariant();
                if (ln == "guid" || ln == "id")
                {
                    var txt = node.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(txt)) return txt;
                }
            }
            return null;
        }

        static string? TryGetAudioUrl(FeedItem item)
        {
            // use raw xml to include vendor extensions
            var root = item.SpecificItem?.Element as XElement;
            if (root != null)
            {
                // enclosure or media content with audio type or audio-looking url
                foreach (var node in root.Descendants())
                {
                    var ln = node.Name.LocalName.ToLowerInvariant();
                    if (ln is "enclosure" or "content")
                    {
                        var candidateUrl = node.Attribute("url")?.Value;
                        var type = node.Attribute("type")?.Value;
                        if (!string.IsNullOrWhiteSpace(candidateUrl) &&
                            (string.IsNullOrWhiteSpace(type) ||
                             type.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ||
                             IsAudioUrl(candidateUrl)))
                            return candidateUrl;
                    }
                }

                // fallback: link element with audio-looking url
                var linkEl = root.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase));
                var linkUrl = linkEl?.Attribute("href")?.Value ?? linkEl?.Value;
                if (!string.IsNullOrWhiteSpace(linkUrl) && IsAudioUrl(linkUrl)) return linkUrl;
            }

            return null;
        }

        // duration parsing
        static long? ParseDurationToMs(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();

            // plain seconds like 1234
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secOnly) && secOnly >= 0)
                return secOnly * 1000L;

            // hh:mm:ss or mm:ss
            var parts = s.Split(':');
            if (parts.Length == 2 || parts.Length == 3)
            {
                if (int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ss) &&
                    int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm))
                {
                    int hh = 0;
                    if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hh)) return null;
                    if (ss < 0 || mm < 0 || hh < 0) return null;
                    long totalSec = hh * 3600L + mm * 60L + ss;
                    return totalSec * 1000L;
                }
            }

            // iso 8601 like PT1H23M45S
            try
            {
                if (s.StartsWith("P", StringComparison.OrdinalIgnoreCase))
                {
                    var ts = System.Xml.XmlConvert.ToTimeSpan(s);
                    if (ts >= TimeSpan.Zero) return (long)ts.TotalMilliseconds;
                }
            }
            catch { }

            return null;
        }

        // itunes:duration, media:content@duration, enclosure@duration
        static long? TryGetDurationMs(FeedItem item)
        {
            var root = item.SpecificItem?.Element;
            if (root is null) return null;

            foreach (var node in root.Descendants())
            {
                var ln = node.Name.LocalName.ToLowerInvariant();
                var ns = (node.Name.NamespaceName ?? "").ToLowerInvariant();

                // itunes duration element
                if (ln == "duration" && ns.Contains("itunes"))
                {
                    var txt = node.Value?.Trim();
                    var parsed = ParseDurationToMs(txt);
                    if (parsed is long ms1) return ms1;
                }

                // media content duration attribute
                if (ln == "content" && ns.Contains("media"))
                {
                    var durAttr = node.Attribute("duration")?.Value;
                    var parsed = ParseDurationToMs(durAttr);
                    if (parsed is long ms2) return ms2;
                }

                // enclosure duration attribute
                if (ln == "enclosure")
                {
                    var durAttr = node.Attribute("duration")?.Value;
                    var parsed = ParseDurationToMs(durAttr);
                    if (parsed is long ms3) return ms3;
                }
            }

            return null;
        }

        static bool IsAudioUrl(string s) =>
            s.EndsWith(".mp3", true, CultureInfo.InvariantCulture) ||
            s.EndsWith(".m4a", true, CultureInfo.InvariantCulture) ||
            s.EndsWith(".aac", true, CultureInfo.InvariantCulture) ||
            s.EndsWith(".ogg", true, CultureInfo.InvariantCulture) ||
            s.EndsWith(".opus", true, CultureInfo.InvariantCulture);

        static string HtmlToText(string html)
        {
            try
            {
                var parser = new HtmlParser();
                var doc = parser.ParseDocument(html ?? "");
                return doc.Body?.TextContent?.Trim() ?? "";
            }
            catch
            {
                // naive strip fallback
                return (html ?? "")
                    .Replace("<br>", " ")
                    .Replace("<br/>", " ")
                    .Replace("<br />", " ")
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Trim();
            }
        }

        #endregion

        #region dispose

        public void Dispose()
        {
            try { _http.Dispose(); } catch { /* best effort */ }
        }

        #endregion
    }
}
