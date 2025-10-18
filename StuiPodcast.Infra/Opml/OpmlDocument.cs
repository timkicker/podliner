using System;
using System.Collections.Generic;
using System.Linq;

namespace StuiPodcast.Infra.Opml
{
    /// <summary>
    /// Repräsentiert ein OPML-Dokument mit flacher Feed-Liste (Gruppen werden bewusst ignoriert).
    /// </summary>
    public sealed class OpmlDocument
    {
        /// <summary>Optionale Überschrift im OPML-Header (&lt;head&gt;&lt;title&gt;…)</summary>
        public string? Title { get; set; }

        /// <summary>Optionales Erstellungs-/Export-Datum (&lt;head&gt;&lt;dateCreated&gt;…)</summary>
        public DateTimeOffset? DateCreated { get; set; }

        /// <summary>Alle Feeds (flach, ohne Ordner/Gruppen).</summary>
        public List<OpmlEntry> Entries { get; } = new();

        /// <summary>Anzahl der Einträge (Bequemlichkeit).</summary>
        public int Count => Entries.Count;

        /// <summary>
        /// Fügt einen Eintrag hinzu (Nulls werden ignoriert).
        /// </summary>
        public void Add(OpmlEntry? entry)
        {
            if (entry is not null) Entries.Add(entry);
        }

        /// <summary>
        /// Liefert alle formal gültigen Einträge (xmlUrl vorhanden & plausibel).
        /// </summary>
        public IEnumerable<OpmlEntry> ValidEntries() => Entries.Where(e => e.IsValid());

        /// <summary>
        /// Liefert alle formal ungültigen Einträge (z. B. fehlende/kaputte xmlUrl).
        /// </summary>
        public IEnumerable<OpmlEntry> InvalidEntries() => Entries.Where(e => !e.IsValid());
    }

    /// <summary>
    /// Ein einzelner OPML-Feed-Eintrag (&lt;outline xmlUrl="…" title="…" htmlUrl="…" type="rss"/&gt;).
    /// </summary>
    public sealed class OpmlEntry
    {
        /// <summary>Pflichtfeld: URL des RSS/Atom-Feeds (http/https).</summary>
        public string? XmlUrl { get; set; }

        /// <summary>Optionale Website-URL (Landing-Page des Feeds).</summary>
        public string? HtmlUrl { get; set; }

        /// <summary>Optionaler Titel/Name des Feeds. Kann leer sein – der Parser darf das setzen.</summary>
        public string? Title { get; set; }

        /// <summary>Optionaler Typ-Hinweis (z. B. "rss", "atom"). Wird beim Export ggf. auf "rss" normalisiert.</summary>
        public string? Type { get; set; }

        /// <summary>
        /// Formale Mindestvalidierung: xmlUrl vorhanden, absolute URI, Schema http/https.
        /// (Keine Online-Validierung.)
        /// </summary>
        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(XmlUrl)) return false;

            if (!Uri.TryCreate(XmlUrl.Trim(), UriKind.Absolute, out var uri))
                return false;

            var scheme = uri.Scheme.ToLowerInvariant();
            return scheme == "http" || scheme == "https";
        }

        /// <summary>
        /// Liefert eine kanonische Vergleichs-URL (für Duplikaterkennung):
        /// - Trim
        /// - Host lowercased
        /// - Fragment entfernt
        /// - Schema beibehalten (kein https-Enforce hier)
        /// </summary>
        public string CanonicalUrl()
        {
            var raw = (XmlUrl ?? string.Empty).Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

            // Host lowercased, Fragment entfernt
            var builder = new UriBuilder(uri)
            {
                Host = uri.Host.ToLowerInvariant(),
                Fragment = string.Empty
            };
            return builder.Uri.ToString();
        }

        public override string ToString()
            => $"{Title ?? "(untitled)"} <{XmlUrl ?? "∅"}>";
    }
}
