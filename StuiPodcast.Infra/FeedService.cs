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
    public class FeedService
    {
        #region fields and ctor

        private readonly AppData _data;
        private readonly AppFacade _app;
        private readonly HttpClient _http;

        public FeedService(AppData data, AppFacade app)
        {
            _data = data;
            _app = app;

            // own http client with decompression and timeouts
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            _http = new HttpClient(handler)
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

        #endregion

        #region public api

        public async Task RemoveFeedAsync(Guid feedId, bool removeDownloads = false)
        {
            var epIds = _data.Episodes.Where(e => e.FeedId == feedId).Select(e => e.Id).ToHashSet();

            var removedFeed = _app.RemoveFeed(feedId);
            var removedEps = _app.RemoveEpisodesByFeed(feedId);
            var removedFromQ = _app.QueueRemoveByEpisodeIds(epIds);

            _data.Feeds.RemoveAll(f => f.Id == feedId);
            _data.Episodes.RemoveAll(e => e.FeedId == feedId);
            _data.Queue.RemoveAll(id => epIds.Contains(id));
            if (_data.LastSelectedFeedId == feedId) _data.LastSelectedFeedId = null;

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
            var saved = _app.AddOrUpdateFeed(probeFeed);

            // update ui cache
            UpsertFeedIntoData(saved);

            // best effort first refresh
            try { await RefreshFeedAsync(saved).ConfigureAwait(false); }
            catch { /* ignored */ }

            return saved;
        }

        public async Task RefreshAllAsync()
        {
            // intentionally sequential
            foreach (var feed in _data.Feeds.ToList())
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
                var persistedFail = _app.AddOrUpdateFeed(feed);
                UpsertFeedIntoData(persistedFail);
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
                var persistedFail = _app.AddOrUpdateFeed(feed);
                UpsertFeedIntoData(persistedFail);
                return;
            }

            // gently update and persist feed metadata
            if (string.IsNullOrWhiteSpace(feed.Title))
                feed.Title = f.Title ?? feed.Url;
            feed.LastChecked = DateTimeOffset.Now;

            var persistedFeed = _app.AddOrUpdateFeed(feed);
            UpsertFeedIntoData(persistedFeed);

            int added = 0, updated = 0, skippedNoAudio = 0;

            // process items (unchanged from before)
            foreach (var item in f.Items ?? Array.Empty<FeedItem>())
            {
                var audioUrl = TryGetAudioUrl(item);
                if (string.IsNullOrWhiteSpace(audioUrl)) { skippedNoAudio++; continue; }

                var pub = ParseDate(item);
                var lenMs = TryGetDurationMs(item) ?? 0;
                var desc = HtmlToText(item.Content ?? item.Description ?? "");
                var title = item.Title ?? "(untitled)";

                var existing = _data.Episodes.FirstOrDefault(e =>
                    e.FeedId == persistedFeed.Id &&
                    string.Equals(e.AudioUrl, audioUrl, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    var ep = new Episode
                    {
                        Id = Guid.NewGuid(),
                        FeedId = persistedFeed.Id,
                        Title = title,
                        AudioUrl = audioUrl,
                        PubDate = pub,
                        DurationMs = lenMs,
                        DescriptionText = desc,
                        Saved = false,
                        Progress = new EpisodeProgress()
                    };

                    var persistedEp = _app.AddOrUpdateEpisode(ep);
                    _data.Episodes.Add(persistedEp);
                    added++;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(title))
                        existing.Title = title;
                    if (!existing.PubDate.HasValue && pub.HasValue)
                        existing.PubDate = pub;
                    if (string.IsNullOrWhiteSpace(existing.DescriptionText) && !string.IsNullOrWhiteSpace(desc))
                        existing.DescriptionText = desc;
                    if (existing.DurationMs <= 0 && lenMs > 0)
                        existing.DurationMs = lenMs;

                    var persistedEp = _app.AddOrUpdateEpisode(existing);
                    // _data holds reference; nothing to add
                    updated++;
                }
            }

            Log.Information("feed/refresh summary id={Id} added={Added} updated={Updated} skippedNoAudio={Skipped}",
                feed.Id, added, updated, skippedNoAudio);
        }

        #endregion

        #region helpers

        void UpsertFeedIntoData(CoreFeed saved)
        {
            var df = _data.Feeds.FirstOrDefault(x => x.Id == saved.Id);
            if (df == null) _data.Feeds.Add(saved);
            else
            {
                df.Title = saved.Title;
                df.Url = saved.Url;
                df.LastChecked = saved.LastChecked;
            }
        }

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
    }
}
