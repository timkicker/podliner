namespace StuiPodcast.App.Bootstrap;

// ==========================================================
// CLI
// ==========================================================
sealed class CliEntrypoint
{
    internal sealed class Options
    {
        public string? Engine;
        public string? Theme;
        public string? Feed;
        public string? Search;
        public string? OpmlImport;
        public string? OpmlImportMode;
        public string? OpmlExport;
        public bool Offline;
        public bool Ascii;
        public string? LogLevel;
        public bool ShowVersion;
        public bool ShowHelp;
    }

    public static Options Parse(string[]? args)
    {
        var o = new Options();
        if (args == null || args.Length == 0) return o;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--version":
                case "-v":
                case "-V": o.ShowVersion = true; break;

                case "--help":
                case "-h":
                case "-?": o.ShowHelp = true; break;

                case "--engine":
                    if (i + 1 < args.Length) o.Engine = args[++i].Trim().ToLowerInvariant();
                    break;

                case "--theme":
                    if (i + 1 < args.Length) o.Theme = args[++i].Trim().ToLowerInvariant();
                    break;

                case "--feed":
                    if (i + 1 < args.Length) o.Feed = args[++i].Trim();
                    break;

                case "--search":
                    if (i + 1 < args.Length) o.Search = args[++i];
                    break;

                case "--opml-import":
                    if (i + 1 < args.Length) o.OpmlImport = args[++i];
                    break;

                case "--import-mode":
                    if (i + 1 < args.Length) o.OpmlImportMode = args[++i].Trim().ToLowerInvariant();
                    break;

                case "--opml-export":
                    if (i + 1 < args.Length) o.OpmlExport = args[++i];
                    break;

                case "--offline": o.Offline = true; break;
                case "--ascii":   o.Ascii   = true; break;

                case "--log-level":
                    if (i + 1 < args.Length) o.LogLevel = args[++i].Trim().ToLowerInvariant();
                    break;
            }
        }
        return o;
    }
}