using System.Security.Cryptography;
using System.Text;

namespace StuiPodcast.Infra.Download
{
    public static class DownloadPathSanitizer
    {
        #region public api

        // safe file name, no directories
        public static string SanitizeFileName(string? name, int maxBytesUtf8 = 240)
        {
            name ??= "untitled";
            name = name.Trim();
            name = name.Normalize(NormalizationForm.FormKC);
            name = RemoveControlChars(name);
            name = RemoveInvalidPathChars(name);

            if (IsWindows)
            {
                name = StripTrailingDotsAndSpaces(name);
                if (IsWindowsReservedName(Path.GetFileNameWithoutExtension(name)))
                    name = "_" + name;
            }

            if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)))
                name = "untitled" + Path.GetExtension(name);

            return TruncateUtf8WithHash(name, maxBytesUtf8);
        }

        // pick extension from hint or url, includes dot, default .bin
        public static string GetExtension(string? urlOrFileHint, string? fallback = ".bin")
        {
            var ext = ExtractExtension(urlOrFileHint);
            if (!string.IsNullOrEmpty(ext)) return NormalizeExt(ext);

            if (!string.IsNullOrEmpty(urlOrFileHint))
            {
                var i = urlOrFileHint.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    var sub = urlOrFileHint.Substring(i + 9);
                    var stop = sub.IndexOfAny(new[] { '&', ';', ' ', '\'', '"' });
                    if (stop > 0) sub = sub.Substring(0, stop);
                    var e2 = ExtractExtension(sub);
                    if (!string.IsNullOrEmpty(e2)) return NormalizeExt(e2);
                }
            }

            return NormalizeExt(fallback ?? ".bin");
        }

        // build os safe download path
        public static string BuildDownloadPath(string baseDir,
                                               string? feedTitle,
                                               string episodeTitle,
                                               string? urlOrExtHint,
                                               bool allowWindowsLongPathPrefix = true)
        {
            baseDir = baseDir ?? "";
            var feedSafe = SanitizeDirectoryName(feedTitle ?? "feed");
            var fileBase = SanitizeFileName(episodeTitle);
            var ext = GetExtension(urlOrExtHint, ".mp3");

            feedSafe = TruncateUtf8WithHash(feedSafe, 120);
            fileBase = TruncateUtf8WithHash(Path.GetFileNameWithoutExtension(fileBase), 200) + ext;

            var full = Path.Combine(baseDir, feedSafe, fileBase);

            if (IsWindows && allowWindowsLongPathPrefix && full.Length >= 260 && Path.IsPathFullyQualified(full))
                full = ApplyLongPathPrefix(full);

            return full;
        }

        // ensure unique path by adding numeric suffix
        public static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);

            for (int i = 2; i < 10_000; i++)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
            }
            return Path.Combine(dir, $"{name}-{ShortHash(path)}{ext}");
        }

        // windows long path prefix
        public static string ApplyLongPathPrefix(string absolutePath)
        {
            if (!IsWindows) return absolutePath;
            if (absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal)) return absolutePath;

            if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + absolutePath.TrimStart('\\');

            return @"\\?\" + absolutePath;
        }

        // safe directory name
        public static string SanitizeDirectoryName(string? name, int maxBytesUtf8 = 200)
        {
            name ??= "untitled";
            name = name.Trim();
            name = name.Normalize(NormalizationForm.FormKC);
            name = RemoveControlChars(name);
            name = RemoveInvalidPathChars(name);

            if (IsWindows)
            {
                name = StripTrailingDotsAndSpaces(name);
                if (IsWindowsReservedName(name)) name = "_" + name;
            }

            if (string.IsNullOrWhiteSpace(name)) name = "untitled";
            return TruncateUtf8WithHash(name, maxBytesUtf8);
        }

        #endregion
        
        #region helpers

        private static bool IsWindows => OperatingSystem.IsWindows();

        private static string RemoveControlChars(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (!char.IsControl(ch) || ch == '\n' || ch == '\r' || ch == '\t')
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string RemoveInvalidPathChars(string s)
        {
            var invalid = new System.Collections.Generic.HashSet<char>(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()));
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch == '/' || ch == '\\' || invalid.Contains(ch))
                    sb.Append('_');
                else
                    sb.Append(ch);
            }
            return CollapseRuns(sb.ToString(), '_');
        }

        private static string StripTrailingDotsAndSpaces(string s) => s.TrimEnd(' ', '.');

        private static bool IsWindowsReservedName(string? nameNoExt)
        {
            if (string.IsNullOrWhiteSpace(nameNoExt)) return false;
            var n = nameNoExt.Trim().ToUpperInvariant();
            string[] reserved =
            {
                "CON","PRN","AUX","NUL",
                "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
                "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
            };
            return reserved.Contains(n);
        }

        private static string TruncateUtf8WithHash(string s, int maxBytesUtf8)
        {
            maxBytesUtf8 = Math.Max(12, maxBytesUtf8);

            var utf8 = Encoding.UTF8;
            var bytes = utf8.GetBytes(s);
            if (bytes.Length <= maxBytesUtf8) return s;

            var suffix = "-" + ShortHash(s);
            var suffixBytesLen = utf8.GetByteCount(suffix);

            var target = maxBytesUtf8 - suffixBytesLen;
            if (target <= 0) target = maxBytesUtf8;

            var cutIndex = FindCutIndex(bytes, target);
            var head = utf8.GetString(bytes.AsSpan(0, cutIndex));

            if (IsWindows) head = StripTrailingDotsAndSpaces(head);
            if (string.IsNullOrEmpty(head)) head = "x";

            return head + (target < maxBytesUtf8 ? suffix : "");
        }

        private static int FindCutIndex(byte[] bytes, int target)
        {
            if (target >= bytes.Length) return bytes.Length;
            var i = target;
            while (i > 0 && (bytes[i] & 0b1100_0000) == 0b1000_0000) i--;
            if (i <= 0) return target;
            return i;
        }

        private static string CollapseRuns(string s, char ch)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            bool last = false;
            foreach (var c in s)
            {
                if (c == ch)
                {
                    if (!last) sb.Append(c);
                    last = true;
                }
                else
                {
                    sb.Append(c);
                    last = false;
                }
            }
            return sb.ToString();
        }

        private static string ExtractExtension(string? hint)
        {
            if (string.IsNullOrWhiteSpace(hint)) return "";

            try
            {
                if (Uri.TryCreate(hint, UriKind.Absolute, out var uri))
                {
                    var p = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
                    var ext = Path.GetExtension(p);
                    if (!string.IsNullOrEmpty(ext)) return ext;

                    var raw = uri.GetLeftPart(UriPartial.Path);
                    ext = Path.GetExtension(raw);
                    return ext ?? "";
                }
            }
            catch { }

            try
            {
                var ext = Path.GetExtension(hint);
                return ext ?? "";
            }
            catch { return ""; }
        }

        private static string NormalizeExt(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return ".bin";
            ext = ext.Trim();
            ext = ext.Trim('"', '\'', ' ', ';');

            if (!ext.StartsWith(".")) ext = "." + ext;
            ext = new string(ext.Where(c => char.IsLetterOrDigit(c) || c == '.').ToArray());
            if (ext.Length == 0 || ext == ".") return ".bin";
            if (ext.Length > 10) ext = ext.Substring(0, 10);
            return ext.ToLowerInvariant();
        }

        private static string ShortHash(string s)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));

            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var sb = new StringBuilder(10);
            int bits = 0, value = 0;

            foreach (var b in bytes)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5 && sb.Length < 10)
                {
                    sb.Append(alphabet[(value >> (bits - 5)) & 31]);
                    bits -= 5;
                }
                if (sb.Length >= 10) break;
            }
            return sb.ToString();
        }
        #endregion
    }
}
