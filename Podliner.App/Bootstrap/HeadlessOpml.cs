using Podliner.App.Services;
using Podliner.Core;
using Podliner.Infra.Opml;
using Serilog;

namespace Podliner.App.Bootstrap;

// --opml-import and --opml-export without a terminal: do the work, print what
// happened, exit.
//
// Homebrew's formula test runs the binary with no terminal and needs one real
// action that ends on its own. Both flags only ran once the TUI was up, so
// without a terminal they did nothing, and without mpv or vlc podliner never
// got that far. This runs before an audio engine is looked for, and never
// touches the network: imported feeds are fetched on the next start.
internal static class HeadlessOpml
{
    public static bool Wants(CliEntrypoint.Options cli)
        => !string.IsNullOrWhiteSpace(cli.OpmlImport) || !string.IsNullOrWhiteSpace(cli.OpmlExport);

    // Import first, so one run can import a file and export the result.
    public static int Run(CliEntrypoint.Options cli, IFeedStore feeds, IEpisodeStore episodes,
                          AppData data, Action save, TextWriter @out, TextWriter err)
    {
        if (!string.IsNullOrWhiteSpace(cli.OpmlImport))
        {
            var code = Import(cli.OpmlImport!, (cli.OpmlImportMode ?? "merge").Trim().ToLowerInvariant(),
                              feeds, episodes, data, save, @out, err);
            if (code != 0) return code;
        }

        if (!string.IsNullOrWhiteSpace(cli.OpmlExport))
            return Export(cli.OpmlExport!, feeds, @out, err);

        return 0;
    }

    static int Import(string path, string mode, IFeedStore feeds, IEpisodeStore episodes,
                      AppData data, Action save, TextWriter @out, TextWriter err)
    {
        if (mode is not ("merge" or "replace" or "dry-run"))
        {
            err.WriteLine($"podliner: unknown --import-mode '{mode}' (expected merge|replace|dry-run)");
            return 1;
        }

        OpmlDocument doc;
        try { doc = OpmlParser.Parse(OpmlIo.ReadFile(path)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "cli/opml headless import failed path={Path}", path);
            err.WriteLine($"podliner: could not read {path}: {ex.Message}");
            return 1;
        }

        if (mode == "replace")
        {
            foreach (var f in feeds.Snapshot().ToList())
            {
                episodes.RemoveByFeed(f.Id);
                feeds.Remove(f.Id);
            }
        }

        var plan = OpmlImportPlanner.Plan(doc, feeds.Snapshot());
        var summary = $"{plan.NewCount} new, {plan.DuplicateCount} already there, {plan.InvalidCount} invalid";

        if (mode == "dry-run")
        {
            @out.WriteLine($"{path}: {summary} (dry run, nothing changed)");
            return 0;
        }

        foreach (var item in plan.NewItems())
        {
            var url = item.Entry.XmlUrl!.Trim();
            var title = string.IsNullOrWhiteSpace(item.Entry.Title) ? url : item.Entry.Title!.Trim();
            feeds.AddOrUpdate(new Feed { Title = title, Url = url });
        }

        // The new feeds have no episodes; make the next start fetch them
        // whatever the refresh interval says.
        if (plan.NewCount > 0) data.LastRefreshAt = null;

        save();
        Log.Information("cli/opml headless import path={Path} mode={Mode} new={New}", path, mode, plan.NewCount);
        @out.WriteLine($"imported {path}: {summary}");
        return 0;
    }

    static int Export(string path, IFeedStore feeds, TextWriter @out, TextWriter err)
    {
        try
        {
            var list = feeds.Snapshot();
            var used = OpmlIo.WriteFile(path, OpmlExporter.BuildXml(list, "podliner feeds"),
                                        sanitizeFileNameIfNeeded: true, overwrite: true);
            Log.Information("cli/opml headless export path={Path} feeds={Count}", used, list.Count);
            @out.WriteLine($"exported {list.Count} feeds to {used}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "cli/opml headless export failed path={Path}", path);
            err.WriteLine($"podliner: could not write {path}: {ex.Message}");
            return 1;
        }
    }
}
