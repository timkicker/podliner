
using System.Reflection;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Opml
{
    // builds a flat opml document from existing feeds
    public static class OpmlExporter
    {
        // create an opml document from a flat list of feeds
        // takes title from the feed when present
        // exports only feeds with valid http or https urls
        public static OpmlDocument FromFeeds(IEnumerable<Feed> feeds, string? documentTitle = "podliner feeds")
        {
            var doc = new OpmlDocument
            {
                Title = documentTitle,
                DateCreated = DateTimeOffset.Now
            };

            if (feeds == null) return doc;

            foreach (var f in feeds)
            {
                var url = GetFeedUrlRobust(f);
                if (!IsHttpUrl(url)) continue;

                var title = GetFeedTitleRobust(f);

                doc.Add(new OpmlEntry
                {
                    XmlUrl = url!.Trim(),
                    HtmlUrl = null,                     // not in model currently
                    Title = string.IsNullOrWhiteSpace(title) ? null : title!.Trim(),
                    Type  = "rss"                       // neutral default
                });
            }

            return doc;
        }

        // build opml xml string utf-8, opml 2.0
        public static string BuildXml(IEnumerable<Feed> feeds, string? documentTitle = "podliner feeds")
            => OpmlParser.Build(FromFeeds(feeds, documentTitle));

        // internals

        private static bool IsHttpUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
            var s = u.Scheme.ToLowerInvariant();
            return s == "http" || s == "https";
        }

        // get feed url via reflection as property names can vary
        // prefers: url, feedurl, xmlurl, sourceurl, rssurl
        private static string? GetFeedUrlRobust(Feed f)
        {
            if (f == null) return null;

            var t = f.GetType();
            string[] names = { "Url", "FeedUrl", "XmlUrl", "SourceUrl", "RssUrl" };

            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p != null && p.PropertyType == typeof(string))
                {
                    var val = (string?)p.GetValue(f);
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }

            // fallback: first string property ending with "url"
            var anyUrl = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                          .FirstOrDefault(pi => pi.PropertyType == typeof(string) &&
                                                pi.Name.EndsWith("Url", StringComparison.OrdinalIgnoreCase));
            return (string?)anyUrl?.GetValue(f);
        }

        // get feed title via reflection (title or name), otherwise null
        private static string? GetFeedTitleRobust(Feed f)
        {
            if (f == null) return null;

            var t = f.GetType();
            string[] names = { "Title", "Name" };

            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p != null && p.PropertyType == typeof(string))
                {
                    var val = (string?)p.GetValue(f);
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }

            return null;
        }
    }
}
