using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StuiPodcast.Infra;

/// <summary>
/// Cross-Platform Pfad-/Dateinamen-Sanitizer für Downloads.
/// - Entfernt/ersetzt unzulässige Zeichen.
/// - Verhindert Windows-Reserved-Names (CON, AUX, COM1, NUL, …).
/// - Schneidet sicher auf sinnvolle Längen mit Hash-Suffix.
/// - Erzeugt konsistente Dateiendungen aus URL/Hint.
/// - Optional: Long-Path-Präfix für Windows (> 260).
/// </summary>
public static class PathSanitizer
{
    // ---------- Public API ----------

    /// <summary>
    /// Liefert einen sicheren Dateinamen (ohne Verzeichnisanteil).
    /// </summary>
    public static string SanitizeFileName(string? name, int maxBytesUtf8 = 240)
    {
        name ??= "untitled";
        name = name.Trim();

        // Unicode normalize, Steuerzeichen raus
        name = name.Normalize(NormalizationForm.FormKC);
        name = RemoveControlChars(name);

        // Verbotene Zeichen je OS
        name = RemoveInvalidPathChars(name);

        // Windows Besonderheiten
        if (IsWindows)
        {
            name = StripTrailingDotsAndSpaces(name);
            if (IsWindowsReservedName(Path.GetFileNameWithoutExtension(name)))
                name = "_" + name;
        }

        // Leere Namen verhindern
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)))
            name = "untitled" + Path.GetExtension(name);

        // Bytes begrenzen (UTF-8), mit Hash-Suffix falls nötig
        return TruncateUtf8WithHash(name, maxBytesUtf8);
    }

    /// <summary>
    /// Ermittelt eine Dateiendung aus URL/Hint. Gibt einen Punkt zurück (".mp3").
    /// Fällt auf ".bin" zurück, wenn nichts ermittelbar.
    /// </summary>
    public static string GetExtension(string? urlOrFileHint, string? fallback = ".bin")
    {
        // 1) Explizite Endung aus Hint/URL ziehen
        var ext = ExtractExtension(urlOrFileHint);
        if (!string.IsNullOrEmpty(ext)) return NormalizeExt(ext);

        // 2) Falls Query-Parameter "filename=" existiert (Content-Disposition-Stil in URLs)
        if (!string.IsNullOrEmpty(urlOrFileHint))
        {
            var q = urlOrFileHint.AsSpan();
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

    /// <summary>
    /// Baut einen OS-sicheren Download-Pfad: baseDir / feed / file.ext
    /// und kürzt Teilstücke sinnvoll. Optional wird ein Windows-LongPath-Präfix verwendet.
    /// </summary>
    public static string BuildDownloadPath(string baseDir,
                                           string? feedTitle,
                                           string episodeTitle,
                                           string? urlOrExtHint,
                                           bool allowWindowsLongPathPrefix = true)
    {
        baseDir = baseDir ?? "";
        var feedSafe = SanitizeDirectoryName(feedTitle ?? "feed");
        var fileBase = SanitizeFileName(episodeTitle);
        var ext      = GetExtension(urlOrExtHint, ".mp3");

        // Einzelteile begrenzen, damit der gesamte Pfad nicht explodiert
        feedSafe = TruncateUtf8WithHash(feedSafe, 120);
        fileBase = TruncateUtf8WithHash(Path.GetFileNameWithoutExtension(fileBase), 200) + ext;

        var full = Path.Combine(baseDir, feedSafe, fileBase);

        if (IsWindows && allowWindowsLongPathPrefix && full.Length >= 260 && Path.IsPathFullyQualified(full))
            full = ApplyLongPathPrefix(full);

        return full;
    }

    /// <summary>
    /// Liefert einen nicht-kollidierenden Pfad, indem "(2)", "(3)" … angefügt wird.
    /// Prüft sowohl Dateien als auch Verzeichnisse.
    /// </summary>
    public static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var dir  = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);

        for (int i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
        // Fallback mit Hash
        return Path.Combine(dir, $"{name}-{ShortHash(path)}{ext}");
    }

    /// <summary>
    /// Für Windows: Präfix "\\?\" anfügen. Andere Systeme: no-op.
    /// </summary>
    public static string ApplyLongPathPrefix(string absolutePath)
    {
        if (!IsWindows) return absolutePath;
        if (absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal)) return absolutePath;

        // UNC vs. lokaler Pfad
        if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + absolutePath.TrimStart('\\');

        return @"\\?\" + absolutePath;
    }

    /// <summary>
    /// Sanitizer für Verzeichnisnamen (ohne Extension-Logik).
    /// </summary>
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

    // ---------- Internals ----------

    private static bool IsWindows =>
        OperatingSystem.IsWindows();

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
        // OS-übergreifend erst mal path separators neutralisieren
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()));
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '/' || ch == '\\' || invalid.Contains(ch))
                sb.Append('_');
            else
                sb.Append(ch);
        }
        // Doppel-Underscores glätten
        return CollapseRuns(sb.ToString(), '_');
    }

    private static string StripTrailingDotsAndSpaces(string s)
    {
        // Windows erlaubt keine endständigen '.' oder ' '
        return s.TrimEnd(' ', '.');
    }

    private static bool IsWindowsReservedName(string? nameNoExt)
    {
        if (string.IsNullOrWhiteSpace(nameNoExt)) return false;
        var n = nameNoExt.Trim().ToUpperInvariant();

        // Geräte & Spezialdateien
        // Quelle: https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file
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
        // Zu kurze Limits abfangen
        maxBytesUtf8 = Math.Max(12, maxBytesUtf8);

        var utf8 = Encoding.UTF8;
        var bytes = utf8.GetBytes(s);
        if (bytes.Length <= maxBytesUtf8) return s;

        // Hash für Eindeutigkeit
        var suffix = "-" + ShortHash(s);
        var suffixBytes = utf8.GetBytes(suffix);

        // Kürzen bis das reinpasst
        int target = maxBytesUtf8 - suffixBytes.Length;
        if (target <= 0) target = maxBytesUtf8; // worst-case, opfern Hash

        // Zeichen-genau kürzen (auf Codepoint-Grenzen)
        int cutIndex = FindCutIndex(bytes, target);
        var head = utf8.GetString(bytes.AsSpan(0, cutIndex));

        // Erneut Windows-Ende säubern
        if (IsWindows) head = StripTrailingDotsAndSpaces(head);
        if (string.IsNullOrEmpty(head)) head = "x";

        var result = head + (target < maxBytesUtf8 ? suffix : "");
        return result;
    }

    private static int FindCutIndex(byte[] bytes, int target)
    {
        if (target >= bytes.Length) return bytes.Length;
        // Für UTF-8 am Anfang eines Zeichen beginnen: Rückwärts bis auf Startbyte (10xxxxxx = continuation)
        int i = target;
        while (i > 0 && (bytes[i] & 0b1100_0000) == 0b1000_0000) i--;
        if (i <= 0) return target; // notfalls roh
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
            // 1) Als URI interpretieren
            if (Uri.TryCreate(hint, UriKind.Absolute, out var uri))
            {
                // Prefer path part
                var p = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
                var ext = Path.GetExtension(p);
                if (!string.IsNullOrEmpty(ext)) return ext;

                // Fallback: letztes '.' in voller URL (ohne Query/Fragment)
                var raw = uri.GetLeftPart(UriPartial.Path);
                ext = Path.GetExtension(raw);
                return ext ?? "";
            }
        }
        catch { /* ignore */ }

        // 2) Als Pfad/Simple-String
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

        // Strip weird quotes
        ext = ext.Trim('"', '\'', ' ', ';');

        if (!ext.StartsWith(".")) ext = "." + ext;
        // Basic Sanitize
        ext = new string(ext.Where(c => char.IsLetterOrDigit(c) || c == '.').ToArray());
        if (ext.Length == 0 || ext == ".") return ".bin";
        if (ext.Length > 10) ext = ext.Substring(0, 10); // unrealistisch lange Extensions vermeiden
        return ext.ToLowerInvariant();
    }

    private static string ShortHash(string s)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        // 10 Zeichen Base32 Crockford (kompakt, ohne / + etc.)
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var sb = new StringBuilder(10);
        int i = 0, bits = 0, value = 0;
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
}
