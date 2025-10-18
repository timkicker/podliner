using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Opml
{
    /// <summary>
    /// Erzeugt aus der bestehenden Feed-Liste ein OPML-Dokument (flach, ohne Gruppen).
    /// </summary>
    public static class OpmlExporter
    {
        /// <summary>
        /// Baut ein <see cref="OpmlDocument"/> aus einer Liste deiner Feeds (flat).
        /// - Nimmt Titel (falls vorhanden) 1:1 aus dem Feed.
        /// - Exportiert nur Feeds mit formal gültiger http/https-URL.
        /// - Setzt <see cref="OpmlDocument.Title"/> optional für den OPML-Header.
        /// </summary>
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
                if (!IsHttpUrl(url)) continue; // nur valide http/https Feeds

                var title = GetFeedTitleRobust(f);

                doc.Add(new OpmlEntry
                {
                    XmlUrl = url!.Trim(),
                    HtmlUrl = null,                     // aktuell nicht im Modell vorhanden
                    Title = string.IsNullOrWhiteSpace(title) ? null : title!.Trim(),
                    Type  = "rss"                       // neutral; OPML-Reader kommen damit i. d. R. klar
                });
            }

            return doc;
        }

        /// <summary>
        /// Komfort: Erzeugt direkt den OPML-XML-String (UTF-8, OPML 2.0) aus Feeds.
        /// </summary>
        public static string BuildXml(IEnumerable<Feed> feeds, string? documentTitle = "podliner feeds")
            => OpmlParser.Build(FromFeeds(feeds, documentTitle));

        // -------------------- Internals --------------------

        private static bool IsHttpUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
            var s = u.Scheme.ToLowerInvariant();
            return s == "http" || s == "https";
        }

        /// <summary>
        /// Holt die Feed-URL robust via Reflection (da Property-Namen im Modell variieren können).
        /// Bevorzugt: Url, FeedUrl, XmlUrl, SourceUrl, RssUrl
        /// </summary>
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

            // Fallback: erste *Url-String-Property
            var anyUrl = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                          .FirstOrDefault(pi => pi.PropertyType == typeof(string) &&
                                                pi.Name.EndsWith("Url", StringComparison.OrdinalIgnoreCase));
            return (string?)anyUrl?.GetValue(f);
        }

        /// <summary>
        /// Holt den Feed-Titel robust via Reflection (Title/Name), sonst null.
        /// </summary>
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
