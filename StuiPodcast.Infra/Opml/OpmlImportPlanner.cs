using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Opml
{
    // import status for a single opml entry
    public enum OpmlImportStatus
    {
        New,
        Duplicate, // already present or duplicate within the same opml
        Invalid
    }

    // planned import item with diagnosis
    public sealed class OpmlImportItem
    {
        public OpmlEntry Entry { get; init; } = null!;
        public OpmlImportStatus Status { get; init; }
        public string? Reason { get; init; }

        // if marked duplicate, points to the existing feed when available
        public Feed? ExistingMatch { get; init; }

        // true if a title update is recommended (only when planner was built with updatetitles=true)
        public bool UpdateTitleRecommended { get; init; }
    }

    // result of an opml import plan (preview)
    public sealed class OpmlImportPlan
    {
        public IReadOnlyList<OpmlImportItem> Items { get; init; } = Array.Empty<OpmlImportItem>();
        public int NewCount       { get; init; }
        public int DuplicateCount { get; init; }
        public int InvalidCount   { get; init; }

        // entries with status new (ready for addfeedasync)
        public IEnumerable<OpmlImportItem> NewItems() => Items.Where(i => i.Status == OpmlImportStatus.New);

        // entries detected as duplicates
        public IEnumerable<OpmlImportItem> DuplicateItems() => Items.Where(i => i.Status == OpmlImportStatus.Duplicate);

        // formally invalid entries
        public IEnumerable<OpmlImportItem> InvalidItems() => Items.Where(i => i.Status == OpmlImportStatus.Invalid);

        // title update candidates (only set when updatetitles=true)
        public IEnumerable<OpmlImportItem> TitleUpdateCandidates() => Items.Where(i => i.UpdateTitleRecommended);
    }

    // builds an idempotent import preview against an existing feed list
    public static class OpmlImportPlanner
    {
        // plan rules:
        // - new: xmlurl valid and not in existing list and not seen earlier in the same opml
        // - duplicate: already in library or appears multiple times in the same opml
        // - invalid: xmlurl missing or invalid
        // when updatetitles=true, plan marks sensible candidates via updatetitlerecommended
        public static OpmlImportPlan Plan(OpmlDocument doc, IReadOnlyList<Feed> existingFeeds, bool updateTitles = false)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            existingFeeds ??= Array.Empty<Feed>();

            // existing feeds by canonical url
            var existingByCanon = BuildExistingMap(existingFeeds);

            // process opml entries and detect duplicates within the file
            var seenCanon = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<OpmlImportItem>(doc.Count);

            foreach (var entry in doc.Entries)
            {
                if (!entry.IsValid())
                {
                    items.Add(new OpmlImportItem
                    {
                        Entry = entry,
                        Status = OpmlImportStatus.Invalid,
                        Reason = "invalid xmlurl"
                    });
                    continue;
                }

                var canon = entry.CanonicalUrl();

                // already in library
                if (existingByCanon.TryGetValue(canon, out var existing))
                {
                    items.Add(new OpmlImportItem
                    {
                        Entry = entry,
                        Status = OpmlImportStatus.Duplicate,
                        Reason = "already in library",
                        ExistingMatch = existing,
                        UpdateTitleRecommended = updateTitles && ShouldUpdateTitle(existing, entry)
                    });
                    continue;
                }

                // duplicate within opml
                if (!seenCanon.Add(canon))
                {
                    items.Add(new OpmlImportItem
                    {
                        Entry = entry,
                        Status = OpmlImportStatus.Duplicate,
                        Reason = "duplicate in opml"
                    });
                    continue;
                }

                // new
                items.Add(new OpmlImportItem
                {
                    Entry = entry,
                    Status = OpmlImportStatus.New,
                    Reason = "new"
                });
            }

            return new OpmlImportPlan
            {
                Items = items,
                NewCount = items.Count(i => i.Status == OpmlImportStatus.New),
                DuplicateCount = items.Count(i => i.Status == OpmlImportStatus.Duplicate),
                InvalidCount = items.Count(i => i.Status == OpmlImportStatus.Invalid)
            };
        }

        // internals

        private static Dictionary<string, Feed> BuildExistingMap(IReadOnlyList<Feed> existingFeeds)
        {
            var dict = new Dictionary<string, Feed>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in existingFeeds)
            {
                var url = GetFeedUrlRobust(f);
                if (string.IsNullOrWhiteSpace(url)) continue;

                var canon = CanonicalizeUrl(url);
                if (canon.Length == 0) continue;

                if (!dict.ContainsKey(canon))
                    dict[canon] = f;
            }
            return dict;
        }

        // get feed url via reflection; property names can vary
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
            if (anyUrl != null)
            {
                var val = (string?)anyUrl.GetValue(f);
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            return null;
        }

        // canonicalize url similar to opmlentry.canonicalurl
        // trim, absolute uri, lowercase host, remove fragment, keep scheme
        static string CanonicalizeUrl(string raw)
        {
            raw = raw?.Trim() ?? "";
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

            var ub = new UriBuilder(uri)
            {
                Host = uri.Host.ToLowerInvariant(),
                Fragment = string.Empty
            };
            return ub.Uri.ToString(); // query remains preserved
        }

        // whether a title update would be useful when updatetitles=true
        // entry has a title and existing title is missing or different (trim and case-insensitive)
        private static bool ShouldUpdateTitle(Feed existing, OpmlEntry entry)
        {
            var newTitle = (entry.Title ?? "").Trim();
            if (newTitle.Length == 0) return false;

            var existingTitle = GetFeedTitleRobust(existing)?.Trim() ?? "";
            if (existingTitle.Length == 0) return true;

            return !string.Equals(existingTitle, newTitle, StringComparison.OrdinalIgnoreCase);
        }

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
