using Serilog;

namespace StuiPodcast.Infra.Player
{
    // resolves libvlc and plugin directories on windows and macos
    // sets env vars and returns optional --plugin-path option
    // usually no changes needed on linux
    public static class VlcPathResolver
    {
        public sealed class Result
        {
            public string? LibDir { get; init; }
            public string? PluginDir { get; init; }
            // options for libvlc constructor
            public string[] LibVlcOptions { get; init; } = Array.Empty<string>();
            // short text for logs or osd
            public string Diagnose { get; init; } = "";
        }

        #region public api

        // run heuristics and apply minimal env changes
        // call early before libvlc loads
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
            // respect env if already set, only extend path
            var envPlugin = GetEnv("VLC_PLUGIN_PATH");
            var envLib    = GetEnv("LIBVLC_LIB_DIR");

            // collect candidates
            var candidates = new List<string>();

            void AddIfExists(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                if (Directory.Exists(p)) candidates.Add(Normalize(p));
            }

            // program files
            AddIfExists(Path.Combine(GetEnv("ProgramFiles"), "VideoLAN", "VLC"));
            AddIfExists(Path.Combine(GetEnv("ProgramFiles(x86)"), "VideoLAN", "VLC"));

            // winget or user installs
            AddIfExists(Path.Combine(GetEnv("LocalAppData"), "Programs", "VideoLAN", "VLC"));
            AddIfExists(Path.Combine(GetEnv("LocalAppData"), "Programs", "VLC"));

            // scan path for vlc.exe
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
                catch { }
            }

            // dedupe and prefer entries that contain plugins
            candidates = candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(d => Directory.Exists(Path.Combine(d, "plugins")))
                .ToList();

            // choose dirs
            string? libDir    = FirstNonEmpty(envLib, candidates.FirstOrDefault());
            string? pluginDir = FirstNonEmpty(envPlugin,
                                  candidates.Select(c => Path.Combine(c, "plugins"))
                                            .FirstOrDefault(Directory.Exists));

            // extend path so libvlc.dll can be found
            if (!string.IsNullOrWhiteSpace(libDir))
                TryPrependToPath(libDir);

            // set plugin path if found
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
            // respect env if already set
            var envPlugin = GetEnv("VLC_PLUGIN_PATH");
            var envLib    = GetEnv("LIBVLC_LIB_DIR");

            var candidatesApp = new[]
            {
                "/Applications/VLC.app/Contents/MacOS",
                $"{GetEnv("HOME")}/Applications/VLC.app/Contents/MacOS",
            };

            // homebrew prefixes for arm and intel
            var brewPrefixes = new[]
            {
                "/opt/homebrew",
                "/usr/local"
            };

            var brewCandidates = brewPrefixes
                .SelectMany(pref => new[]
                {
                    $"{pref}/Cellar/vlc",
                    $"{pref}/opt/vlc",
                })
                .Where(Directory.Exists)
                .SelectMany(root => SafeEnumerateDirectories(root))
                .Where(dir => Directory.Exists(Path.Combine(dir, "lib")))
                .ToList();

            // lib dir candidates
            var libDirs = new List<string>();
            libDirs.AddRange(candidatesApp.Where(Directory.Exists));
            libDirs.AddRange(brewCandidates.Select(d => Path.Combine(d, "lib")).Where(Directory.Exists));

            // plugin dir candidates
            var pluginDirs = new List<string>();
            pluginDirs.AddRange(candidatesApp.Select(d => Path.Combine(d, "plugins")).Where(Directory.Exists));
            pluginDirs.AddRange(brewCandidates.Select(d => Path.Combine(d, "lib", "vlc", "plugins")).Where(Directory.Exists));

            string? libDir    = FirstNonEmpty(envLib, libDirs.FirstOrDefault());
            string? pluginDir = FirstNonEmpty(envPlugin, pluginDirs.FirstOrDefault());

            // set plugin path; dyld vars are often blocked by sip
            if (!string.IsNullOrWhiteSpace(pluginDir))
                TrySetEnv("VLC_PLUGIN_PATH", pluginDir);

            // some p invoke searches benefit from lib dir in path
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
            // libvlc usually finds libs and plugins on linux
            // still respect user provided plugin path
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

                // already present
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

        #endregion
    }
}
