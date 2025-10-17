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
    public FeedService(AppData data) => _data = data;

    // ---------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------

    public async Task<CoreFeed> AddFeedAsync(string url)
    {
        // Quick probe (best effort) um harte Fehler früh zu fangen
        var feed = new CoreFeed { Url = url, Title = url, LastChecked = DateTimeOffset.Now };

        try
        {
            var f = await FeedReader.ReadAsync(url).ConfigureAwait(false);

            feed.Title = string.IsNullOrWhiteSpace(f.Title) ? url : f.Title!;
            feed.LastChecked = DateTimeOffset.Now;
        }
        catch
        {
            // Wenn der erste Read scheitert, lassen wir den Feed trotzdem anlegen,
            // der Nutzer kann später refreshen; Title bleibt auf URL-Fallback.
            feed.LastChecked = DateTimeOffset.Now;
        }

        _data.Feeds.Add(feed);

        // Erster Bestückungs-Refresh (best effort)
        try { await RefreshFeedAsync(feed).ConfigureAwait(false); } catch { /* sichtbar in UI erst beim nächsten Refresh */ }

        return feed;
    }

    public async Task RefreshAllAsync()
    {
        // bewusst sequentiell (einfacher, weniger Serverdruck)
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
            return; // still scheitern – UI bleibt nutzbar
        }

        // Feed-Metadaten sanft aktualisieren
        if (string.IsNullOrWhiteSpace(feed.Title))
            feed.Title = f.Title ?? feed.Url;
        feed.LastChecked = DateTimeOffset.Now;

        // Items verarbeiten
        foreach (var item in f.Items ?? Array.Empty<FeedItem>())
        {
            var audioUrl = TryGetAudioUrl(item);
            if (string.IsNullOrWhiteSpace(audioUrl)) continue;

            var pub = ParseDate(item);
            var lenMs = TryGetDurationMs(item);
            var desc = HtmlToText(item.Content ?? item.Description ?? "");
            var title = item.Title ?? "(untitled)";

            // episodes sind durch (FeedId + AudioUrl) identifizierbar
            var existing = _data.Episodes.FirstOrDefault(e => e.FeedId == feed.Id && string.Equals(e.AudioUrl, audioUrl, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                _data.Episodes.Add(new Episode
                {
                    FeedId = feed.Id,
                    Title = title,
                    PubDate = pub,
                    AudioUrl = audioUrl!,
                    DescriptionText = desc,
                    LengthMs = lenMs
                });
            }
            else
            {
                // Sanft updaten: nur fehlende Felder/nützlichere Werte eintragen
                if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(title))
                    existing.Title = title;

                if (!existing.PubDate.HasValue && pub.HasValue)
                    existing.PubDate = pub;

                if (string.IsNullOrWhiteSpace(existing.DescriptionText) && !string.IsNullOrWhiteSpace(desc))
                    existing.DescriptionText = desc;

                if ((!existing.LengthMs.HasValue || existing.LengthMs <= 0) && (lenMs.HasValue && lenMs > 0))
                    existing.LengthMs = lenMs;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

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
        // Arbeite auf dem Raw-XML, um alle Vendor-Erweiterungen mitzunehmen
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

    // Sammelt Dauer aus itunes:duration, media:content@duration, alternativen Attributen
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

            // (3) Weitere Varianten (seltener): <enclosure duration="...">
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
            // Fallback: naive Strip
            return (html ?? "").Replace("<br>", " ").Replace("<br/>", " ").Replace("<br />", " ")
                               .Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
