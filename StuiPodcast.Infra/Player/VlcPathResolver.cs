using Serilog;

namespace StuiPodcast.Infra.Player
{
    // resolves libvlc and plugin directories on windows/mac/linux
    // sets env vars and returns optional --plugin-path option
    public static class VlcPathResolver
    {
        public sealed class Result
        {
            public string? LibDir { get; init; }
            public string? PluginDir { get; init; }
            public string[] LibVlcOptions { get; init; } = Array.Empty<string>();
            public string Diagnose { get; init; } = "";
        }

        #region public api

        public static Result Apply()
        {
            try
            {
                if (OperatingSystem.IsWindows())   return ApplyWindows();
                if (OperatingSystem.IsMacOS())     return ApplyMacOS();
                if (OperatingSystem.IsLinux())     return ApplyLinux();

                Log.Information("VLC: unsupported OS family; no path changes");
                return new Result { Diagnose = "no op unsupported os family" };
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "VLC: resolver exception");
                return new Result { Diagnose = "resolver exception see logs" };
            }
        }

        #endregion

        #region windows

        private static Result ApplyWindows()
        {
            var envPlugin = GetEnv("VLC_PLUGIN_PATH");
            var envLib    = GetEnv("LIBVLC_LIB_DIR");

            var candidates = new List<string>();

            void AddIfExists(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                if (Directory.Exists(p)) candidates.Add(Normalize(p));
            }

            AddIfExists(Path.Combine(GetEnv("ProgramFiles") ?? string.Empty, "VideoLAN", "VLC"));
            AddIfExists(Path.Combine(GetEnv("ProgramFiles(x86)") ?? string.Empty, "VideoLAN", "VLC"));
            AddIfExists(Path.Combine(GetEnv("LocalAppData") ?? string.Empty, "Programs", "VideoLAN", "VLC"));
            AddIfExists(Path.Combine(GetEnv("LocalAppData") ?? string.Empty, "Programs", "VLC"));

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
                catch
                {
                    // ignored
                }
            }

            candidates = candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(d => Directory.Exists(Path.Combine(d, "plugins")))
                .ToList();

            string? libDir    = FirstNonEmpty(envLib, candidates.FirstOrDefault());
            string? pluginDir = FirstNonEmpty(envPlugin,
                                  candidates.Select(c => Path.Combine(c, "plugins"))
                                            .FirstOrDefault(Directory.Exists));

            if (!string.IsNullOrWhiteSpace(libDir))
                TryPrependToPath(libDir);

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

        #endregion

        #region macos

        private static Result ApplyMacOS()
        {
            var envPlugin = GetEnv("VLC_PLUGIN_PATH");
            var envLib    = GetEnv("LIBVLC_LIB_DIR");

            // App-Bundle (direkt oder via Brew-Cask)
            var appMacOS = new[]
            {
                "/Applications/VLC.app/Contents/MacOS",
                $"{GetEnv("HOME")}/Applications/VLC.app/Contents/MacOS"
            };

            static IEnumerable<string> MacPluginCandidates(string macOSDir)
            {
                yield return Path.Combine(macOSDir, "plugins");                              // …/Contents/MacOS/plugins
                yield return Path.Combine(macOSDir, "..", "Resources", "plugins");          // …/Contents/Resources/plugins
            }

            // Homebrew Formula (nicht Cask)
            var brewPrefixes = new[] { "/opt/homebrew", "/usr/local" };
            var brewRoots = brewPrefixes
                .SelectMany(p => new[] { $"{p}/Cellar/vlc", $"{p}/opt/vlc" })
                .Where(Directory.Exists)
                .SelectMany(SafeEnumerateDirectories)
                .ToList();

            var libDirs = new List<string>();
            libDirs.AddRange(appMacOS.Where(Directory.Exists));                                   // …/Contents/MacOS
            libDirs.AddRange(brewRoots.Select(d => Path.Combine(d, "lib")).Where(Directory.Exists));
            libDirs.AddRange(brewRoots.Select(d => Path.Combine(d, "Frameworks")).Where(Directory.Exists));

            var pluginDirs = new List<string>();
            foreach (var mac in appMacOS.Where(Directory.Exists))
                pluginDirs.AddRange(MacPluginCandidates(mac).Where(Directory.Exists));
            pluginDirs.AddRange(brewRoots.Select(d => Path.Combine(d, "lib", "vlc", "plugins")).Where(Directory.Exists));

            string? libDir    = FirstNonEmpty(envLib, libDirs.FirstOrDefault());
            string? pluginDir = FirstNonEmpty(envPlugin, pluginDirs.FirstOrDefault());

            if (!string.IsNullOrWhiteSpace(pluginDir))
                TrySetEnv("VLC_PLUGIN_PATH", pluginDir);

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

        #endregion

        #region linux

        private static Result ApplyLinux()
        {
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

        #endregion

        #region helpers

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

        private static string Normalize(string path) =>
            path.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

        private static string? FirstNonEmpty(params string?[] items)
            => items.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        #endregion
    }
}
