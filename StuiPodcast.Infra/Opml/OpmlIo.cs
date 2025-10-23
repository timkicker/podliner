using StuiPodcast.Infra.Download;
using System;
using System.IO;
using System.Text;

namespace StuiPodcast.Infra.Opml
{
    public static class OpmlIo
    {
        // read file as text - utf 8 with bom detection - expands tilde to user home
        public static string ReadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is empty", nameof(path));
            path = ExpandHome(path);

            using var fs = File.OpenRead(path);
            using var sr = new StreamReader(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }

        // write text as utf 8 without bom
        // sanitizes leaf name and ensures .opml extension when requested
        // creates missing directories and returns the final path
        public static string WriteFile(string path, string content, bool sanitizeFileNameIfNeeded = true, bool overwrite = true)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            content ??= string.Empty;

            path = ExpandHome(path);
            if (sanitizeFileNameIfNeeded) path = SanitizeLeafToOpml(path);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // windows long path support
            if (OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path))
                path = DownloadPathSanitizer.ApplyLongPathPrefix(path);

            // atomic like write via tmp then move
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

        // default export path in documents or home
        // targetDir null or empty uses documents
        // baseName sanitized and forced to .opml if needed
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

        #region  helpers


        private static string ExpandHome(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;

            // expand tilde to current user home
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

            // add .opml if no extension
            if (string.IsNullOrEmpty(Path.GetExtension(leaf)))
                leaf += ".opml";

            // sanitize leaf
            leaf = DownloadPathSanitizer.SanitizeFileName(leaf);

            // ensure .opml extension
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
        #endregion
    }
}
