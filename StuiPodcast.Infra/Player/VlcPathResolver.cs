using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace StuiPodcast.Infra.Player;

/// <summary>
/// Findet die LibVLC- und Plugin-Verzeichnisse auf Windows/macOS (best effort),
/// setzt nötige Umgebungsvariablen (PATH / VLC_PLUGIN_PATH) und liefert eine
/// optionale "--plugin-path=…" Option für den LibVLC-Konstruktor.
/// Unter Linux ist in der Regel nichts zu tun.
/// </summary>
public static class VlcPathResolver
{
    public sealed class Result
    {
        public string? LibDir { get; init; }
        public string? PluginDir { get; init; }
        /// <summary>LibVLC-Startoptionen (kann in LibVLC(..) durchgereicht werden)</summary>
        public string[] LibVlcOptions { get; init; } = Array.Empty<string>();
        /// <summary>Kurztext für Logs/OSD</summary>
        public string Diagnose { get; init; } = "";
    }

    /// <summary>
    /// Führt die Heuristiken aus und nimmt minimale Env-Anpassungen vor.
    /// Ruf das möglichst früh auf (bevor LibVLC geladen wird).
    /// </summary>
    public static Result Apply()
    {
        try
        {
            if (OperatingSystem.IsWindows())   return ApplyWindows();
            if (OperatingSystem.IsMacOS())     return ApplyMacOS();
            if (OperatingSystem.IsLinux())     return ApplyLinux();

            Log.Information("VLC: unsupported OS family; no path changes");
            return new Result { Diagnose = "no-op (unsupported OS family)" };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "VLC: resolver exception");
            return new Result { Diagnose = "resolver exception (see logs)" };
        }
    }

    // ---------------------------- Windows ----------------------------

    private static Result ApplyWindows()
    {
        // 1) Wenn bereits gesetzt, respektieren – nur noch PATH ergänzen
        var envPlugin = GetEnv("VLC_PLUGIN_PATH");
        var envLib    = GetEnv("LIBVLC_LIB_DIR"); // nicht standardisiert, aber oft genutzt

        // 2) Kandidaten sammeln
        var candidates = new List<string>();

        void AddIfExists(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            if (Directory.Exists(p)) candidates.Add(Normalize(p));
        }

        // Program Files (x64/x86)
        AddIfExists(Path.Combine(GetEnv("ProgramFiles"), "VideoLAN", "VLC"));
        AddIfExists(Path.Combine(GetEnv("ProgramFiles(x86)"), "VideoLAN", "VLC"));

        // Winget / User-Install (häufig unter LocalAppData\Programs)
        AddIfExists(Path.Combine(GetEnv("LocalAppData"), "Programs", "VideoLAN", "VLC"));
        AddIfExists(Path.Combine(GetEnv("LocalAppData"), "Programs", "VLC"));

        // PATH scannen (falls vlc.exe drin liegt)
        foreach (var dir in (GetEnv("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var d = dir?.Trim();
                if (string.IsNullOrEmpty(d)) continue;
                var vlcExe = Path.Combine(d, "vlc.exe");
                if (File.Exists(vlcExe))
                {
                    var baseDir = Normalize(Path.GetDirectoryName(vlcExe)!);
                    if (Directory.Exists(baseDir)) candidates.Add(baseDir);
                }
            }
            catch { /* ignore */ }
        }

        // deduplizieren, nach "existiert plugins" sortieren
        candidates = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => Directory.Exists(Path.Combine(d, "plugins")))
            .ToList();

        // 3) Entscheiden
        string? libDir    = FirstNonEmpty(envLib, candidates.FirstOrDefault());
        string? pluginDir = FirstNonEmpty(envPlugin,
                              candidates.Select(c => Path.Combine(c, "plugins"))
                                        .FirstOrDefault(Directory.Exists));

        // 4) PATH erweitern (damit libvlc.dll gefunden wird)
        if (!string.IsNullOrWhiteSpace(libDir))
        {
            TryPrependToPath(libDir);
        }

        // 5) VLC_PLUGIN_PATH setzen, wenn wir eins gefunden haben
        if (!string.IsNullOrWhiteSpace(pluginDir))
            TrySetEnv("VLC_PLUGIN_PATH", pluginDir);

        var opts = new List<string>();
        if (!string.IsNullOrWhiteSpace(pluginDir))
            opts.Add($"--plugin-path={pluginDir}");

        var diag = $"win libDir={libDir ?? "∅"} pluginDir={pluginDir ?? "∅"}";
        Log.Information("VLC resolver: {Diag}", diag);

        return new Result
        {
            LibDir = libDir,
            PluginDir = pluginDir,
            LibVlcOptions = opts.ToArray(),
            Diagnose = diag
        };
    }

    // ---------------------------- macOS ----------------------------

    private static Result ApplyMacOS()
    {
        // 1) Bereits gesetzte Variablen respektieren
        var envPlugin = GetEnv("VLC_PLUGIN_PATH");
        var envLib    = GetEnv("LIBVLC_LIB_DIR"); // informell, aber nützlich

        var candidatesApp = new[]
        {
            "/Applications/VLC.app/Contents/MacOS",                               // Standard App
            $"{GetEnv("HOME")}/Applications/VLC.app/Contents/MacOS",              // User-Apps
        };

        // Homebrew (ARM64 & Intel)
        var brewPrefixes = new[]
        {
            "/opt/homebrew",        // Apple Silicon
            "/usr/local"            // Intel
        };

        var brewCandidates = brewPrefixes
            .SelectMany(pref => new[]
            {
                $"{pref}/Cellar/vlc",
                $"{pref}/opt/vlc",                 // symlink
            })
            .Where(Directory.Exists)
            .SelectMany(root => SafeEnumerateDirectories(root))
            .Where(dir => Directory.Exists(Path.Combine(dir, "lib")))
            .ToList();

        // libDir-Kandidaten
        var libDirs = new List<string>();

        // App: lib liegt direkt unter MacOS
        libDirs.AddRange(candidatesApp.Where(Directory.Exists));

        // Brew: …/Cellar/vlc/<ver>/lib
        libDirs.AddRange(brewCandidates.Select(d => Path.Combine(d, "lib")).Where(Directory.Exists));

        // pluginDir-Kandidaten
        var pluginDirs = new List<string>();

        // App: …/MacOS/plugins
        pluginDirs.AddRange(candidatesApp.Select(d => Path.Combine(d, "plugins")).Where(Directory.Exists));

        // Brew: …/Cellar/vlc/<ver>/lib/vlc/plugins
        pluginDirs.AddRange(brewCandidates.Select(d => Path.Combine(d, "lib", "vlc", "plugins")).Where(Directory.Exists));

        string? libDir    = FirstNonEmpty(envLib, libDirs.FirstOrDefault());
        string? pluginDir = FirstNonEmpty(envPlugin, pluginDirs.FirstOrDefault());

        // DYLD_* lässt sich wegen SIP oft nicht zur Laufzeit setzen → wir setzen VLC_PLUGIN_PATH
        if (!string.IsNullOrWhiteSpace(pluginDir))
            TrySetEnv("VLC_PLUGIN_PATH", pluginDir);

        // Manche P/Invoke-Suchen profitieren davon, wenn libDir im PATH auftaucht (Mono/.NET Unterschiede)
        if (!string.IsNullOrWhiteSpace(libDir))
            TryPrependToPath(libDir);

        var opts = new List<string>();
        if (!string.IsNullOrWhiteSpace(pluginDir))
            opts.Add($"--plugin-path={pluginDir}");

        var diag = $"mac libDir={libDir ?? "∅"} pluginDir={pluginDir ?? "∅"}";
        Log.Information("VLC resolver: {Diag}", diag);

        return new Result
        {
            LibDir = libDir,
            PluginDir = pluginDir,
            LibVlcOptions = opts.ToArray(),
            Diagnose = diag
        };
    }

    // ---------------------------- Linux ----------------------------

    private static Result ApplyLinux()
    {
        // In der Regel findet LibVLC unter Linux seine libs/plugins selbst.
        // Falls der Nutzer etwas gesetzt hat, respektieren wir das.
        var envPlugin = GetEnv("VLC_PLUGIN_PATH");
        var opts = new List<string>();
        if (!string.IsNullOrWhiteSpace(envPlugin))
            opts.Add($"--plugin-path={Normalize(envPlugin)}");

        var diag = $"linux pluginDir={envPlugin ?? "∅"}";
        Log.Information("VLC resolver: {Diag}", diag);

        return new Result
        {
            LibDir = null,
            PluginDir = envPlugin,
            LibVlcOptions = opts.ToArray(),
            Diagnose = diag
        };
    }

    // ---------------------------- Helpers ----------------------------

    private static string? GetEnv(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); }
        catch { return null; }
    }

    private static void TrySetEnv(string name, string value)
    {
        try { Environment.SetEnvironmentVariable(name, value); }
        catch (Exception ex) { Log.Debug(ex, "env set failed {Name}", name); }
    }

    private static void TryPrependToPath(string dir)
    {
        try
        {
            var cur = GetEnv("PATH") ?? "";
            var parts = cur.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            // Bereits vorhanden?
            if (parts.Any(p => string.Equals(Normalize(p), Normalize(dir), StringComparison.OrdinalIgnoreCase)))
                return;

            var next = dir + Path.PathSeparator + cur;
            Environment.SetEnvironmentVariable("PATH", next);
        }
        catch (Exception ex) { Log.Debug(ex, "prepend PATH failed"); }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static string Normalize(string path) => path.Replace('\\', Path.DirectorySeparatorChar)
                                                      .Replace('/', Path.DirectorySeparatorChar);

    private static string? FirstNonEmpty(params string?[] items)
        => items.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
}
