using System.Globalization;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using CodeHollow.FeedReader;
using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

// aliases
using CoreFeed = StuiPodcast.Core.Feed;
using FeedItem = CodeHollow.FeedReader.FeedItem;
using RssFeed  = CodeHollow.FeedReader.Feed;

namespace StuiPodcast.Infra
{
    public class FeedService
    {
        #region fields and ctor

        private readonly AppData _data;
        private readonly AppFacade _app;

        public FeedService(AppData data, AppFacade app)
        {
            _data = data;
            _app  = app;
        }

        #endregion

        #region public api
        // FeedService.cs
        public async Task RemoveFeedAsync(Guid feedId, bool removeDownloads = false)
        {
            // 1) Episoden-IDs des Feeds bestimmen (für Queue-Cleanup & evtl. Downloads)
            var epIds = _data.Episodes.Where(e => e.FeedId == feedId).Select(e => e.Id).ToHashSet();

            // 2) Persistenz: Feed + Episoden aus der Library entfernen
            var removedFeed    = _app.RemoveFeed(feedId);               // -> LibraryStore
            var removedEps     = _app.RemoveEpisodesByFeed(feedId);     // -> LibraryStore
            var removedFromQ   = _app.QueueRemoveByEpisodeIds(epIds);   // -> LibraryStore

            // 3) AppData (UI-Cache) spiegeln
            _data.Feeds.RemoveAll(f => f.Id == feedId);
            _data.Episodes.RemoveAll(e => e.FeedId == feedId);
            _data.Queue.RemoveAll(id => epIds.Contains(id));
            if (_data.LastSelectedFeedId == feedId) _data.LastSelectedFeedId = null;

            // 4) Optional: lokale Dateien löschen (wenn gewünscht)
            if (removeDownloads)
            {
                foreach (var id in epIds)
                    if (_app.TryGetLocalPath(id, out var path) && !string.IsNullOrWhiteSpace(path))
                        try { System.IO.File.Delete(path!); } catch { /* best effort */ }
            }

            // 5) Sicher speichern
            _app.SaveNow();

            Serilog.Log.Information("feed/remove persisted id={FeedId} feedRemoved={Feed} episodesRemoved={Eps} queueRemoved={Q}",
                feedId, removedFeed, removedEps, removedFromQ);
        }


        public async Task<CoreFeed> AddFeedAsync(string url)
        {
            Log.Information("feed/add url={Url}", url);

            // quick probe, best effort
            var probeFeed = new CoreFeed { Url = url, Title = url, LastChecked = DateTimeOffset.Now };
            try
            {
                var f = await FeedReader.ReadAsync(url).ConfigureAwait(false);
                Log.Information("feed/probe ok url={Url} title={Title} items={Count}", url, f.Title, f.Items?.Count ?? 0);
                probeFeed.Title = string.IsNullOrWhiteSpace(f.Title) ? url : f.Title!;
                probeFeed.LastChecked = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "feed/probe fail url={Url}", url);
                probeFeed.LastChecked = DateTimeOffset.Now;
            }

            // persistent upsert to get a stable id
            var saved = _app.AddOrUpdateFeed(probeFeed);

            // update ui cache
            UpsertFeedIntoData(saved);

            // best effort first refresh
            try { await RefreshFeedAsync(saved).ConfigureAwait(false); } catch { }

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
            try
            {
                f = await FeedReader.ReadAsync(feed.Url).ConfigureAwait(false);
                Log.Information("feed/refresh probe ok id={Id} title={Title} items={Items}",
                    feed.Id, f.Title ?? feed.Title, f.Items?.Count ?? 0);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "feed/refresh fail url={Url}", feed.Url);
                feed.LastChecked = DateTimeOffset.Now;
                // persist last checked even on failure
                var persistedFail = _app.AddOrUpdateFeed(feed);
                UpsertFeedIntoData(persistedFail);
                return;
            }

            // soft update of feed metadata then persist
            if (string.IsNullOrWhiteSpace(feed.Title))
                feed.Title = f.Title ?? feed.Url;
            feed.LastChecked = DateTimeOffset.Now;

            var persistedFeed = _app.AddOrUpdateFeed(feed);
            UpsertFeedIntoData(persistedFeed);

            int added = 0, updated = 0, skippedNoAudio = 0;

            // process items
            foreach (var item in f.Items ?? Array.Empty<FeedItem>())
            {
                var audioUrl = TryGetAudioUrl(item);
                if (string.IsNullOrWhiteSpace(audioUrl)) { skippedNoAudio++; continue; }

                var pub   = ParseDate(item);
                var lenMs = TryGetDurationMs(item) ?? 0;
                var desc  = HtmlToText(item.Content ?? item.Description ?? "");
                var title = item.Title ?? "(untitled)";

                // identity: feed id + audio url
                var existing = _data.Episodes.FirstOrDefault(e =>
                    e.FeedId == persistedFeed.Id &&
                    string.Equals(e.AudioUrl, audioUrl, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    var ep = new Episode
                    {
                        FeedId          = persistedFeed.Id,
                        Title           = title,
                        PubDate         = pub,
                        AudioUrl        = audioUrl!,
                        DescriptionText = desc,
                        DurationMs      = lenMs
                    };

                    // persistent upsert to get stable id
                    var persistedEp = _app.AddOrUpdateEpisode(ep);

                    // update ui cache with new entry
                    _data.Episodes.Add(persistedEp);
                    added++;
                }
                else
                {
                    // soft updates on existing object
                    if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(title))
                        existing.Title = title;

                    if (!existing.PubDate.HasValue && pub.HasValue)
                        existing.PubDate = pub;

                    if (string.IsNullOrWhiteSpace(existing.DescriptionText) && !string.IsNullOrWhiteSpace(desc))
                        existing.DescriptionText = desc;

                    if (existing.DurationMs <= 0 && lenMs > 0)
                        existing.DurationMs = lenMs;

                    // persist to refresh library.json
                    _app.AddOrUpdateEpisode(existing);
                    updated++;
                }
            }

            Log.Information("feed/refresh done id={Id} title={Title} items={Items} added={Added} updated={Updated} skippedNoAudio={Skipped}",
                feed.Id, feed.Title, f.Items?.Count ?? 0, added, updated, skippedNoAudio);
        }

        #endregion

        #region helpers

        void UpsertFeedIntoData(CoreFeed saved)
        {
            var df = _data.Feeds.FirstOrDefault(x => x.Id == saved.Id);
            if (df == null) _data.Feeds.Add(saved);
            else
            {
                df.Title       = saved.Title;
                df.Url         = saved.Url;
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

        static string? TryGetAudioUrl(FeedItem item)
        {
            // use raw xml to include vendor extensions
            var root = item.SpecificItem?.Element as XElement;
            if (root is null) return null;

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
            var root = item.SpecificItem?.Element as XElement;
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
            s.EndsWith(".mp3",  true, CultureInfo.InvariantCulture) ||
            s.EndsWith(".m4a",  true, CultureInfo.InvariantCulture) ||
            s.EndsWith(".aac",  true, CultureInfo.InvariantCulture) ||
            s.EndsWith(".ogg",  true, CultureInfo.InvariantCulture) ||
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
