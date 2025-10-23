using System.Xml;
using System.Xml.Linq;

namespace StuiPodcast.Infra.Opml
{
    // opml 2.0 parser and serializer for a flat feed list
    public static class OpmlParser
    {
        #region public api

        // parse xml into an OpmlDocument
        // detects feeds via outline@xmlUrl case insensitive
        // reads optional fields title or text and htmlUrl and type
        // flattens nested structures and ignores folders
        // entries with missing or invalid url are kept to show in import plans
        public static OpmlDocument Parse(string xml)
        {
            if (xml == null) throw new ArgumentNullException(nameof(xml));

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            };

            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);

            var xdoc = XDocument.Load(xr, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            var result = new OpmlDocument();

            // head: title and dateCreated optional
            var head = xdoc.Root?.Elements().FirstOrDefault(e => NameIs(e, "head"));
            if (head != null)
            {
                var titleEl = head.Elements().FirstOrDefault(e => NameIs(e, "title"));
                if (titleEl != null)
                {
                    var t = NormalizeText(titleEl.Value);
                    if (!string.IsNullOrWhiteSpace(t))
                        result.Title = t;
                }

                var dateEl = head.Elements().FirstOrDefault(e => NameIs(e, "dateCreated"));
                if (dateEl != null)
                {
                    if (DateTimeOffset.TryParse(dateEl.Value.Trim(), out var dto))
                        result.DateCreated = dto;
                }
            }

            // body: flatten all outline elements
            var body = xdoc.Root?.Elements().FirstOrDefault(e => NameIs(e, "body"));
            if (body != null)
            {
                foreach (var ol in body.Descendants().Where(e => NameIs(e, "outline")))
                    TryAddOutlineAsEntry(ol, result.Entries);
            }
            else
            {
                // fallback when no body exists
                foreach (var ol in xdoc.Descendants().Where(e => NameIs(e, "outline")))
                    TryAddOutlineAsEntry(ol, result.Entries);
            }

            return result;
        }

        // serialize an OpmlDocument to utf 8 opml 2.0
        // invalid entries without usable xmlUrl are skipped
        public static string Build(OpmlDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var xHead = new XElement("head");
            if (!string.IsNullOrWhiteSpace(doc.Title))
                xHead.Add(new XElement("title", doc.Title));
            xHead.Add(new XElement("dateCreated", (doc.DateCreated ?? DateTimeOffset.Now).ToString("r"))); // rfc1123

            var xBody = new XElement("body");

            foreach (var e in doc.ValidEntries())
            {
                var attrs = new List<XAttribute>();

                // set both title and text when present, at least text
                var title = NormalizeText(e.Title);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    attrs.Add(new XAttribute("title", title));
                    attrs.Add(new XAttribute("text", title));
                }
                else
                {
                    // use host as fallback for text
                    var host = TryGetHost(e.XmlUrl) ?? e.XmlUrl ?? "";
                    attrs.Add(new XAttribute("text", host));
                }

                attrs.Add(new XAttribute("type", string.IsNullOrWhiteSpace(e.Type) ? "rss" : e.Type!.Trim()));
                attrs.Add(new XAttribute("xmlUrl", e.XmlUrl!.Trim()));

                var html = NormalizeUrlOrNull(e.HtmlUrl);
                if (!string.IsNullOrWhiteSpace(html))
                    attrs.Add(new XAttribute("htmlUrl", html));

                xBody.Add(new XElement("outline", attrs.ToArray()));
            }

            var root = new XElement("opml",
                new XAttribute("version", "2.0"),
                xHead,
                xBody
            );

            var xdoc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);

            using var sw = new Utf8StringWriter();
            xdoc.Save(sw, SaveOptions.DisableFormatting);
            return sw.ToString();
        }

        #endregion

        #region internals

        private static void TryAddOutlineAsEntry(XElement outline, List<OpmlEntry> target)
        {
            // tolerant attribute access
            var xmlUrl = GetAttr(outline, "xmlUrl");
            if (string.IsNullOrWhiteSpace(xmlUrl))
                xmlUrl = GetAttr(outline, "xmlURL"); // some exporters use this casing

            var title = FirstNonEmpty(
                GetAttr(outline, "title"),
                GetAttr(outline, "text")
            );

            var html = GetAttr(outline, "htmlUrl");
            var type = GetAttr(outline, "type");

            // no xmlUrl attribute at all means folder node which we ignore
            if (xmlUrl is null)
                return;

            // xmlUrl exists but may be empty or invalid
            var entry = new OpmlEntry
            {
                XmlUrl = NormalizeUrlOrNull(xmlUrl),
                HtmlUrl = NormalizeUrlOrNull(html),
                Title = NormalizeText(title),
                Type = NormalizeText(type)
            };

            target.Add(entry);
        }

        private static string? GetAttr(XElement el, string name)
        {
            foreach (var a in el.Attributes())
            {
                if (string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                    return a.Value;
            }
            return null;
        }

        private static bool NameIs(XElement el, string localName)
            => string.Equals(el.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

        private static string? NormalizeText(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        private static string? NormalizeUrlOrNull(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        private static string? TryGetHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
            return u.Host;
        }

        private static string? FirstNonEmpty(params string?[] parts)
        {
            foreach (var p in parts)
                if (!string.IsNullOrWhiteSpace(p)) return p;
            return null;
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        #endregion
    }
}
