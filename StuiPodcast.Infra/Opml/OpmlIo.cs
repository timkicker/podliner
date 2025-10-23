using StuiPodcast.Infra.Download;
using System;
using System.IO;
using System.Text;

namespace StuiPodcast.Infra.Opml
{
    /// <summary>
    /// Datei-IO für OPML: robustes Lesen/Schreiben, Pfad-Sanitizing und Default-Exportpfade.
    /// </summary>
    public static class OpmlIo
    {
        /// <summary>
        /// Liest eine Datei als Text.
        /// - UTF-8 mit BOM-Erkennung (detectEncodingFromByteOrderMarks=true)
        /// - "~" wird auf das User-Home expandiert
        /// </summary>
        public static string ReadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is empty", nameof(path));
            path = ExpandHome(path);

            using var fs = File.OpenRead(path);
            using var sr = new StreamReader(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }

        /// <summary>
        /// Schreibt Text in eine Datei (UTF-8 ohne BOM).
        /// - Sanitized den Dateinamen (nur Leaf) und erzwingt .opml-Extension (falls keine vorhanden).
        /// - Erstellt fehlende Verzeichnisse.
        /// - Wendet auf Windows optional den Long-Path-Prefix an.
        /// Gibt den tatsächlich verwendeten Pfad zurück (nach Sanitize/Prefix).
        /// </summary>
        public static string WriteFile(string path, string content, bool sanitizeFileNameIfNeeded = true, bool overwrite = true)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            content ??= string.Empty;

            path = ExpandHome(path);
            if (sanitizeFileNameIfNeeded) path = SanitizeLeafToOpml(path);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Windows Long-Path Unterstützung
            if (OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path))
                path = DownloadPathSanitizer.ApplyLongPathPrefix(path);

            // Schreiben (overwrite steuert, ob vorhandene Dateien ersetzt werden)
            var tmp = path + ".tmp";
            using (var sw = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                sw.Write(content);
            }

            if (File.Exists(path))
            {
                if (!overwrite)
                    throw new IOException($"File already exists: {path}");
                File.Delete(path);
            }
            File.Move(tmp, path);

            return path;
        }

        /// <summary>
        /// Liefert einen sinnvollen Standard-Exportpfad (z. B. "Dokumente/stui-feeds.opml").
        /// - targetDir null/leer → MyDocuments, sonst Home-Expansion.
        /// - baseName wird sanitizt und um ".opml" ergänzt (falls nötig).
        /// </summary>
        public static string GetDefaultExportPath(string? targetDir = null, string? baseName = "podliner-feeds.opml")
        {
            var dir = string.IsNullOrWhiteSpace(targetDir) ? GetDefaultDir() : ExpandHome(targetDir!);
            Directory.CreateDirectory(dir);

            var name = baseName;
            if (string.IsNullOrWhiteSpace(name)) name = "podliner-feeds.opml";
            name = DownloadPathSanitizer.SanitizeFileName(name!);
            if (!name.EndsWith(".opml", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(Path.GetExtension(name)))
                name += ".opml";

            return Path.Combine(dir, name);
        }

        // -------------------- Internals --------------------

        private static string ExpandHome(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;

            // Unix-Style "~/" und auch "~user" vereinfachen wir auf aktuelles Home
            if (p.StartsWith("~"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? "";
                if (!string.IsNullOrEmpty(home))
                {
                    var rest = p.Substring(1).TrimStart('/', '\\');
                    return Path.Combine(home, rest);
                }
            }
            return p;
        }

        private static string SanitizeLeafToOpml(string path)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            var leaf = Path.GetFileName(path);

            // Falls keine Extension angegeben ist, .opml ergänzen.
            if (string.IsNullOrEmpty(Path.GetExtension(leaf)))
                leaf += ".opml";

            // Leaf sanitisieren (OS-gültiger Name, begrenzt)
            leaf = DownloadPathSanitizer.SanitizeFileName(leaf);

            // Zur Sicherheit: Extension .opml sicherstellen
            if (!leaf.EndsWith(".opml", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = Path.GetFileNameWithoutExtension(leaf);
                leaf = baseName + ".opml";
            }

            return Path.Combine(dir, leaf);
        }

        private static string GetDefaultDir()
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(docs)) return docs;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home)) return home;

            return Directory.GetCurrentDirectory();
        }
    }
}
