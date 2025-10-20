using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using CodeHollow.FeedReader;
using StuiPodcast.Core;

// Aliases
using CoreFeed = StuiPodcast.Core.Feed;
using FeedItem = CodeHollow.FeedReader.FeedItem;
using RssFeed = CodeHollow.FeedReader.Feed;

namespace StuiPodcast.Infra;

public class FeedService
{
    private readonly AppData _data;
    private readonly AppFacade _app;

    public FeedService(AppData data, AppFacade app)
    {
        _data = data;
        _app  = app;
    }

    // ---------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------

    public async Task<CoreFeed> AddFeedAsync(string url)
    {
        // 1) Probe (best effort)
        var probeFeed = new CoreFeed { Url = url, Title = url, LastChecked = DateTimeOffset.Now };
        try
        {
            var f = await FeedReader.ReadAsync(url).ConfigureAwait(false);
            probeFeed.Title = string.IsNullOrWhiteSpace(f.Title) ? url : f.Title!;
            probeFeed.LastChecked = DateTimeOffset.Now;
        }
        catch
        {
            probeFeed.LastChecked = DateTimeOffset.Now;
        }

        // 2) Persistenter Upsert (vergibt stabile Id)
        var saved = _app.AddOrUpdateFeed(probeFeed);

        // 3) UI sofort updaten (Upsert in _data.Feeds)
        var existingInData = _data.Feeds.FirstOrDefault(x => x.Id == saved.Id);
        if (existingInData == null)
            _data.Feeds.Add(saved);
        else
        {
            existingInData.Title       = saved.Title;
            existingInData.Url         = saved.Url;
            existingInData.LastChecked = saved.LastChecked;
        }

        // 4) Erste Befüllung (best effort)
        try { await RefreshFeedAsync(saved).ConfigureAwait(false); } catch { }

        return saved;
    }

    public async Task RefreshAllAsync()
    {
        // bewusst sequentiell
        foreach (var feed in _data.Feeds.ToList())
        {
            try { await RefreshFeedAsync(feed).ConfigureAwait(false); }
            catch { /* pro Feed best-effort */ }
        }
    }

    public async Task RefreshFeedAsync(CoreFeed feed)
    {
        RssFeed f;
        try
        {
            f = await FeedReader.ReadAsync(feed.Url).ConfigureAwait(false);
        }
        catch
        {
            feed.LastChecked = DateTimeOffset.Now;
            // Persistiere trotzdem "LastChecked"
            var persistedFail = _app.AddOrUpdateFeed(feed);
            UpsertFeedIntoData(persistedFail);
            return;
        }

        // Feed-Metadaten sanft aktualisieren + persistieren
        if (string.IsNullOrWhiteSpace(feed.Title))
            feed.Title = f.Title ?? feed.Url;
        feed.LastChecked = DateTimeOffset.Now;

        var persistedFeed = _app.AddOrUpdateFeed(feed);
        UpsertFeedIntoData(persistedFeed);

        // Items verarbeiten
        foreach (var item in f.Items ?? Array.Empty<FeedItem>())
        {
            var audioUrl = TryGetAudioUrl(item);
            if (string.IsNullOrWhiteSpace(audioUrl)) continue;

            var pub   = ParseDate(item);
            var lenMs = (long)(TryGetDurationMs(item) ?? 0);
            var desc  = HtmlToText(item.Content ?? item.Description ?? "");
            var title = item.Title ?? "(untitled)";

            // Ident: (FeedId + AudioUrl)
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

                // Persistenter Upsert → stabile Id vom Store
                var persistedEp = _app.AddOrUpdateEpisode(ep);

                // UI updaten (neuer Eintrag)
                _data.Episodes.Add(persistedEp);
            }
            else
            {
                // Sanfte Updates am vorhandenen Objekt
                if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(title))
                    existing.Title = title;

                if (!existing.PubDate.HasValue && pub.HasValue)
                    existing.PubDate = pub;

                if (string.IsNullOrWhiteSpace(existing.DescriptionText) && !string.IsNullOrWhiteSpace(desc))
                    existing.DescriptionText = desc;

                if (existing.DurationMs <= 0 && lenMs > 0)
                    existing.DurationMs = lenMs;

                // Persistenter Upsert, damit library.json aktualisiert wird
                _app.AddOrUpdateEpisode(existing);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

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
        // Raw-XML, um Vendor-Erweiterungen mitzunehmen
        var root = item.SpecificItem?.Element as XElement;
        if (root is null) return null;

        // 1) <enclosure url="..." type="audio/..."> und <media:content ...>
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

        // 2) Fallback: <link href="..."> oder <link>…</link> mit Audio-Endung
        var linkEl = root.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase));
        var linkUrl = linkEl?.Attribute("href")?.Value ?? linkEl?.Value;
        if (!string.IsNullOrWhiteSpace(linkUrl) && IsAudioUrl(linkUrl)) return linkUrl;

        return null;
    }

    // --- Duration helpers -------------------------------------------------------

    static long? ParseDurationToMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();

        // reine Sekunden: "1234"
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secOnly) && secOnly >= 0)
            return secOnly * 1000L;

        // "HH:MM:SS" oder "MM:SS"
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

        // ISO 8601 Duration wie "PT1H23M45S"
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

            // (1) <itunes:duration>12:34</itunes:duration>
            if (ln == "duration" && ns.Contains("itunes"))
            {
                var txt = node.Value?.Trim();
                var parsed = ParseDurationToMs(txt);
                if (parsed is long ms1) return ms1;
            }

            // (2) <media:content duration="1234" ... />
            if (ln == "content" && ns.Contains("media"))
            {
                var durAttr = node.Attribute("duration")?.Value;
                var parsed = ParseDurationToMs(durAttr);
                if (parsed is long ms2) return ms2;
            }

            // (3) weitere Varianten (selten)
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
            // Fallback: naive Strip
            return (html ?? "")
                .Replace("<br>", " ")
                .Replace("<br/>", " ")
                .Replace("<br />", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }
    }
}
