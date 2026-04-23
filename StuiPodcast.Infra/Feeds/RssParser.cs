using System.Globalization;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using CodeHollow.FeedReader;
using FeedItem = CodeHollow.FeedReader.FeedItem;

namespace StuiPodcast.Infra.Feeds;

// Pure-function RSS/Atom helpers. Every method takes a parsed FeedItem
// (from CodeHollow.FeedReader) and pulls out a specific value, falling
// back to hand-rolled XML walks where the library doesn't surface the
// field we need (e.g. enclosure@duration, vendor-specific itunes:duration,
// guid as text).
//
// No state, no IO — safe to call from any thread. Tests can exercise these
// directly against a FeedReader.ReadFromString(xml) result.
internal static class RssParser
{
    public static DateTimeOffset? ParseDate(FeedItem item)
    {
        if (item.PublishingDate.HasValue) return item.PublishingDate.Value;

        if (!string.IsNullOrWhiteSpace(item.PublishingDateString) &&
            DateTimeOffset.TryParse(item.PublishingDateString, out var d))
            return d;

        return null;
    }

    // RSS2 <guid> (or Atom <id>) — the stable episode identifier the
    // publisher promises not to rotate. Primary dedup key in FeedService's
    // refresh loop; lets us follow CDN migrations transparently.
    // isPermaLink="false" is common (publishers opt out of URI semantics);
    // we don't care either way — we just need the text content.
    public static string? TryGetGuid(FeedItem item)
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

    // <podcast:chapters url="..." type="application/json+chapters"/>
    // Only the URL is meaningful here; we fetch + parse the JSON lazily via
    // ChaptersFetcher so feed refresh stays snappy.
    public static string? TryGetChaptersUrl(FeedItem item)
    {
        var root = item.SpecificItem?.Element as XElement;
        if (root == null) return null;

        foreach (var node in root.Descendants())
        {
            if (!string.Equals(node.Name.LocalName, "chapters", StringComparison.OrdinalIgnoreCase))
                continue;

            // Guard: namespace must be the podcast: namespace or we accept
            // any if no-namespace (some feeds drop namespaces entirely).
            var ns = node.Name.NamespaceName ?? "";
            if (!string.IsNullOrEmpty(ns) && !ns.Contains("podcast", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = node.Attribute("url")?.Value;
            if (!string.IsNullOrWhiteSpace(url)) return url.Trim();
        }
        return null;
    }

    public static string? TryGetAudioUrl(FeedItem item)
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

    // itunes:duration, media:content@duration, enclosure@duration
    public static long? TryGetDurationMs(FeedItem item)
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

    public static long? ParseDurationToMs(string? s)
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

    public static bool IsAudioUrl(string s) =>
        s.EndsWith(".mp3", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".m4a", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".aac", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".ogg", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".opus", true, CultureInfo.InvariantCulture);

    // Strips HTML down to text using AngleSharp (handles entities and
    // nested tags). Falls back to a naive <br> replacement if parsing
    // throws so a single broken description doesn't lose the whole
    // refresh cycle.
    public static string HtmlToText(string html)
    {
        try
        {
            var parser = new HtmlParser();
            var doc = parser.ParseDocument(html ?? "");
            return doc.Body?.TextContent?.Trim() ?? "";
        }
        catch
        {
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
