using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using CodeHollow.FeedReader;
using StuiPodcast.Core;

// Aliases
using CoreFeed = StuiPodcast.Core.Feed;
using FeedItem = CodeHollow.FeedReader.FeedItem;

namespace StuiPodcast.Infra;

public class FeedService {
    private readonly AppData _data;
    public FeedService(AppData data) => _data = data;

    public async Task<CoreFeed> AddFeedAsync(string url) {
        var f = await FeedReader.ReadAsync(url);
        var feed = new CoreFeed {
            Url = url,
            Title = string.IsNullOrWhiteSpace(f.Title) ? url : f.Title!,
            LastChecked = DateTimeOffset.Now
        };
        _data.Feeds.Add(feed);
        await RefreshFeedAsync(feed);
        return feed;
    }

    public async Task RefreshAllAsync() {
        foreach (var feed in _data.Feeds)
            await RefreshFeedAsync(feed);
    }

    public async Task RefreshFeedAsync(CoreFeed feed) {
        var f = await FeedReader.ReadAsync(feed.Url);
        feed.Title = string.IsNullOrWhiteSpace(feed.Title) ? (f.Title ?? feed.Url) : feed.Title;
        feed.LastChecked = DateTimeOffset.Now;

        foreach (var item in f.Items) {
            var audioUrl = TryGetAudioUrl(item);
            if (string.IsNullOrWhiteSpace(audioUrl)) continue;

            var exists = _data.Episodes.Any(e => e.FeedId == feed.Id && e.AudioUrl == audioUrl);
            if (exists) continue;

            _data.Episodes.Add(new Episode {
                FeedId = feed.Id,
                Title = item.Title ?? "(untitled)",
                PubDate = ParseDate(item),
                AudioUrl = audioUrl!, // geprüft
                DescriptionText = HtmlToText(item.Content ?? item.Description ?? "")
            });
        }
    }

    static DateTimeOffset? ParseDate(FeedItem item) {
        if (item.PublishingDate.HasValue) return item.PublishingDate.Value;
        if (!string.IsNullOrWhiteSpace(item.PublishingDateString)
            && DateTimeOffset.TryParse(item.PublishingDateString, out var d)) return d;
        return null;
    }

    static string? TryGetAudioUrl(FeedItem item) {
        // nur über Raw-XML arbeiten
        var root = item.SpecificItem?.Element as XElement;
        if (root is null) return null;

        // 1) <enclosure url="..." type="audio/..."> bzw. <media:content ...>
        foreach (var node in root.Descendants()) {
            var ln = node.Name.LocalName.ToLowerInvariant();
            if (ln is "enclosure" or "content") {
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

    static bool IsAudioUrl(string s) =>
        s.EndsWith(".mp3", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".m4a", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".aac", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".ogg", true, CultureInfo.InvariantCulture) ||
        s.EndsWith(".opus", true, CultureInfo.InvariantCulture);

    static string HtmlToText(string html) {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        return doc.Body?.TextContent?.Trim() ?? "";
    }
}
