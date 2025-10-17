using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StuiPodcast.Core;

namespace StuiPodcast.Infra
{
    /// <summary>
    /// Status eines OPML-Imports für einen einzelnen Eintrag.
    /// </summary>
    public enum OpmlImportStatus
    {
        New,
        Duplicate, // bereits vorhanden ODER Duplikat innerhalb der OPML-Datei
        Invalid
    }

    /// <summary>
    /// Ein geplanter Import-Eintrag mit Diagnose.
    /// </summary>
    public sealed class OpmlImportItem
    {
        public OpmlEntry Entry { get; init; } = null!;
        public OpmlImportStatus Status { get; init; }
        public string? Reason { get; init; }

        /// <summary>Wenn als Duplicate erkannt wurde, verweist dies (falls verfügbar) auf den existierenden Feed.</summary>
        public Feed? ExistingMatch { get; init; }

        /// <summary>
        /// true, wenn (gemäß Plan-Option) das Überschreiben/Setzen des Feed-Titels
        /// sinnvoll wäre (Entry.Title vorhanden & vom bestehenden Titel abweichend/leer).
        /// Wird nur dann auf true gesetzt, wenn der Planner mit updateTitles=true gebaut wurde.
        /// </summary>
        public bool UpdateTitleRecommended { get; init; }
    }

    /// <summary>
    /// Ergebnis eines OPML-Import-Plans (Vorschau).
    /// </summary>
    public sealed class OpmlImportPlan
    {
        public IReadOnlyList<OpmlImportItem> Items { get; init; } = Array.Empty<OpmlImportItem>();
        public int NewCount        { get; init; }
        public int DuplicateCount  { get; init; }
        public int InvalidCount    { get; init; }

        /// <summary>Alle Einträge mit Status New (bereit für AddFeedAsync).</summary>
        public IEnumerable<OpmlImportItem> NewItems() => Items.Where(i => i.Status == OpmlImportStatus.New);

        /// <summary>Alle Einträge, die als Duplicate erkannt wurden.</summary>
        public IEnumerable<OpmlImportItem> DuplicateItems() => Items.Where(i => i.Status == OpmlImportStatus.Duplicate);

        /// <summary>Alle formal ungültigen Einträge.</summary>
        public IEnumerable<OpmlImportItem> InvalidItems() => Items.Where(i => i.Status == OpmlImportStatus.Invalid);

        /// <summary>Kandidaten für Titel-Update (nur gesetzt, wenn updateTitles=true).</summary>
        public IEnumerable<OpmlImportItem> TitleUpdateCandidates() => Items.Where(i => i.UpdateTitleRecommended);
    }

    /// <summary>
    /// Erzeugt aus einem OPML-Dokument eine idempotente Import-Vorschau gegen eine bestehende Feed-Liste.
    /// </summary>
    public static class OpmlImportPlanner
    {
        /// <summary>
        /// Erzeugt einen Plan:
        /// - NEW, wenn xmlUrl formal valide und weder in bestehender Liste noch bereits früher im OPML gesehen.
        /// - DUPLICATE, wenn bereits in Bibliothek ODER die gleiche URL im OPML mehrfach vorkommt.
        /// - INVALID, wenn xmlUrl formal fehlt/ungültig.
        /// Titel werden standardmäßig NICHT überschrieben; wird updateTitles=true gesetzt,
        /// markiert der Plan sinnvolle Kandidaten (UpdateTitleRecommended=true).
        /// </summary>
        public static OpmlImportPlan Plan(OpmlDocument doc, IReadOnlyList<Feed> existingFeeds, bool updateTitles = false)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            existingFeeds ??= Array.Empty<Feed>();

            // 1) Bestehende Feeds in Map (kanonische URL → Feed)
            var existingByCanon = BuildExistingMap(existingFeeds);

            // 2) OPML Einträge verarbeiten; Duplikate innerhalb der OPML-Datei erkennen
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
                        Reason = "invalid xmlUrl"
                    });
                    continue;
                }

                var canon = entry.CanonicalUrl();

                // a) In der Bibliothek vorhanden?
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

                // b) Innerhalb derselben OPML-Datei bereits gesehen?
                if (!seenCanon.Add(canon))
                {
                    items.Add(new OpmlImportItem
                    {
                        Entry = entry,
                        Status = OpmlImportStatus.Duplicate,
                        Reason = "duplicate in OPML"
                    });
                    continue;
                }

                // c) Neu
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
                NewCount       = items.Count(i => i.Status == OpmlImportStatus.New),
                DuplicateCount = items.Count(i => i.Status == OpmlImportStatus.Duplicate),
                InvalidCount   = items.Count(i => i.Status == OpmlImportStatus.Invalid)
            };
        }

        // -------------------- Internals --------------------

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

        /// <summary>
        /// Holt die Feed-URL robust via Reflection (da die Property in den Modellen unterschiedlich benannt sein kann).
        /// Kandidaten: Url, FeedUrl, XmlUrl, SourceUrl, RssUrl
        /// </summary>
        private static string? GetFeedUrlRobust(Feed f)
        {
            if (f == null) return null;

            var t = f.GetType();
            // Häufigste Property-Namen zuerst
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

            // Fallback: Suche nach *Url String-Property
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

        /// <summary>
        /// Kanonisiert eine URL wie OpmlEntry.CanonicalUrl():
        /// - Trim, absolute URI
        /// - Host lowercase
        /// - Fragment entfernt
        /// - Schema bleibt (kein https-Enforce)
        /// </summary>
        private static string CanonicalizeUrl(string raw)
        {
            raw = raw?.Trim() ?? "";
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

            var ub = new UriBuilder(uri)
            {
                Host = uri.Host.ToLowerInvariant(),
                Fragment = string.Empty
            };
            return ub.Uri.ToString();
        }

        /// <summary>
        /// Ob (falls updateTitles=true) ein Titel-Update sinnvoll wäre:
        /// - Entry.Title vorhanden/nicht-blank UND
        /// - Bestehender Titel fehlt oder unterscheidet sich nach Trim/Case-Ignore deutlich.
        /// </summary>
        private static bool ShouldUpdateTitle(Feed existing, OpmlEntry entry)
        {
            var newTitle = (entry.Title ?? "").Trim();
            if (newTitle.Length == 0) return false;

            var existingTitle = GetFeedTitleRobust(existing)?.Trim() ?? "";
            if (existingTitle.Length == 0) return true;

            // Unterschied?
            return !string.Equals(existingTitle, newTitle, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetFeedTitleRobust(Feed f)
        {
            if (f == null) return null;

            var t = f.GetType();
            // Übliche Kandidaten: Title, Name
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
