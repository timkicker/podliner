

namespace StuiPodcast.Infra.Opml
{
    // opml document with a flat feed list; groups are intentionally ignored
    public sealed class OpmlDocument
    {
        // optional title from <head><title>
        public string? Title { get; set; }

        // optional creation or export date from <head><dateCreated>
        public DateTimeOffset? DateCreated { get; set; }

        // all feeds, flat without folders
        public List<OpmlEntry> Entries { get; } = new();

        // convenience count
        public int Count => Entries.Count;

        // add an entry; ignores null
        public void Add(OpmlEntry? entry)
        {
            if (entry is not null) Entries.Add(entry);
        }

        // all formally valid entries (xmlurl present and plausible)
        public IEnumerable<OpmlEntry> ValidEntries() => Entries.Where(e => e.IsValid());

        // all formally invalid entries (for example missing or broken xmlurl)
        public IEnumerable<OpmlEntry> InvalidEntries() => Entries.Where(e => !e.IsValid());
    }

    // single opml feed entry: <outline xmlUrl="..." title="..." htmlUrl="..." type="rss" />
    public sealed class OpmlEntry
    {
        // required feed url, http or https
        public string? XmlUrl { get; set; }

        // optional website url, the landing page
        public string? HtmlUrl { get; set; }

        // optional feed title; may be empty and can be set by the parser
        public string? Title { get; set; }

        // optional type hint, for example "rss" or "atom"; export may normalize to "rss"
        public string? Type { get; set; }

        // minimal formal validation: xmlurl present, absolute uri, scheme is http or https
        // no online validation
        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(XmlUrl)) return false;

            if (!Uri.TryCreate(XmlUrl.Trim(), UriKind.Absolute, out var uri))
                return false;

            var scheme = uri.Scheme.ToLowerInvariant();
            return scheme == "http" || scheme == "https";
        }

        // returns a canonical comparison url for duplicate detection
        // trim, lowercase host, remove fragment, keep original scheme
        public string CanonicalUrl()
        {
            var raw = (XmlUrl ?? string.Empty).Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

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
