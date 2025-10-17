using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace StuiPodcast.Infra
{
    /// <summary>
    /// Parser & Serializer für OPML 2.0 (flache Feed-Liste; Gruppen/Ordner werden ignoriert).
    /// </summary>
    public static class OpmlParser
    {
        /// <summary>
        /// Parst ein OPML-Dokument (als String) in ein <see cref="OpmlDocument"/>.
        /// - Erkennt Feeds anhand outline@xmlUrl (case-insensitive).
        /// - Liest optionale Felder (title/text, htmlUrl, type).
        /// - Verschachtelte Strukturen werden abgeflacht (Gruppen werden ignoriert).
        /// - Einträge mit fehlender/ungültiger URL werden als "invalid" Einträge aufgenommen
        ///   (damit sie im Import-Plan sichtbar gezählt werden können).
        /// </summary>
        public static OpmlDocument Parse(string xml)
        {
            if (xml == null) throw new ArgumentNullException(nameof(xml));

            // Robust gegen DTD/Externals
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            };

            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);

            var xdoc = XDocument.Load(xr, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            var result = new OpmlDocument();

            // HEAD: title, dateCreated (optional)
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

            // BODY: alle outline-Elemente rekursiv flatten
            var body = xdoc.Root?.Elements().FirstOrDefault(e => NameIs(e, "body"));
            if (body != null)
            {
                foreach (var ol in body.Descendants().Where(e => NameIs(e, "outline")))
                {
                    TryAddOutlineAsEntry(ol, result.Entries);
                }
            }
            else
            {
                // fallback: falls kein body, trotzdem alle outlines im Dokument scannen
                foreach (var ol in xdoc.Descendants().Where(e => NameIs(e, "outline")))
                {
                    TryAddOutlineAsEntry(ol, result.Entries);
                }
            }

            return result;
        }

        /// <summary>
        /// Serialisiert ein <see cref="OpmlDocument"/> (UTF-8, OPML 2.0, flache Liste).
        /// Ungültige Einträge (ohne brauchbare xmlUrl) werden ausgelassen.
        /// </summary>
        public static string Build(OpmlDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var xHead = new XElement("head");
            if (!string.IsNullOrWhiteSpace(doc.Title))
                xHead.Add(new XElement("title", doc.Title));
            xHead.Add(new XElement("dateCreated", (doc.DateCreated ?? DateTimeOffset.Now).ToString("r"))); // RFC1123

            var xBody = new XElement("body");

            foreach (var e in doc.ValidEntries())
            {
                var attrs = new List<XAttribute>();

                // title/text – wir setzen beide, falls vorhanden; mindestens aber "text"
                var title = NormalizeText(e.Title);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    attrs.Add(new XAttribute("title", title));
                    attrs.Add(new XAttribute("text", title));
                }
                else
                {
                    // text ist in OPML üblich → leer ist unschön, setze aus URL Host als Fallback
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

            // Als String zurückgeben (UTF-8 Deklaration bleibt in der XML-Declaration)
            using var sw = new Utf8StringWriter();
            xdoc.Save(sw, SaveOptions.DisableFormatting);
            return sw.ToString();
        }

        // -------------------- Internals --------------------

        private static void TryAddOutlineAsEntry(XElement outline, List<OpmlEntry> target)
        {
            // Attribute tolerant (case-insensitive)
            var xmlUrl = GetAttr(outline, "xmlUrl"); // offizieller Name
            if (string.IsNullOrWhiteSpace(xmlUrl))
            {
                // Manche Exporter verwenden "xmlURL" (anderes Casing)
                xmlUrl = GetAttr(outline, "xmlURL");
            }

            var title = FirstNonEmpty(
                GetAttr(outline, "title"),
                GetAttr(outline, "text")
            );

            var html  = GetAttr(outline, "htmlUrl");
            var type  = GetAttr(outline, "type");

            // Wenn keinerlei xmlUrl vorhanden, behandeln wir das als "Ordner" – NICHT als Eintrag.
            // Ausnahme: xmlUrl existiert, ist aber leer/ungültig → wir erzeugen einen "invalid" Eintrag,
            // damit der Import-Plan ihn zählen/anzeigen kann.
            if (xmlUrl is null)
                return; // Ordner-Knoten → ignorieren

            // xmlUrl vorhanden, aber evtl. leer/kaputt → Eintrag erzeugen (IsValid() entscheidet später)
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
            // Case-insensitive Zugriff: wir scannen die Attribute und vergleichen Name ohne NS
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
            // Unicode NFC/KC wäre möglich; für OPML reicht Trim
            return t.Length == 0 ? null : t;
        }

        private static string? NormalizeUrlOrNull(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            // Keine erzwungene https-Umstellung; keine aggressive Normalisierung
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
            {
                if (!string.IsNullOrWhiteSpace(p)) return p;
            }
            return null;
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }
}
