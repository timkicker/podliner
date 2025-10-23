using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Opml;

namespace StuiPodcast.App.Command.Module;

internal static class CmdOpmlModule
{
    public static void ExecOpml(string[] args, UiShell ui, AppData data, Func<Task> persist)
    {
        var argv = args ?? Array.Empty<string>();
        if (argv.Length == 0) { ui.ShowOsd("usage: :opml import <path> [--update-titles] | :opml export [<path>]"); return; }

        var sub = argv[0].ToLowerInvariant();
        if (sub is "import")
        {
            if (argv.Length < 2) { ui.ShowOsd("usage: :opml import <path> [--update-titles]"); return; }

            var path = argv[1];
            bool updateTitles = argv.Any(a => string.Equals(a, "--update-titles", StringComparison.OrdinalIgnoreCase));

            string xml;
            try { xml = OpmlIo.ReadFile(path); }
            catch (Exception ex) { ui.ShowOsd($"import: read error ({ex.Message})", 2000); return; }

            OpmlDocument doc;
            try { doc = OpmlParser.Parse(xml); }
            catch (Exception ex) { ui.ShowOsd($"import: parse error ({ex.Message})", 2000); return; }

            var plan = OpmlImportPlanner.Plan(doc, data.Feeds, updateTitles);
            ui.ShowOsd($"OPML: new {plan.NewCount}, dup {plan.DuplicateCount}, invalid {plan.InvalidCount}", 1600);

            if (plan.NewCount == 0) return;

            int added = 0;
            foreach (var item in plan.NewItems())
            {
                var url = item.Entry.XmlUrl?.Trim();
                if (string.IsNullOrWhiteSpace(url)) continue;

                ui.RequestAddFeed(url);
                added++;
            }

            _ = persist();
            ui.RequestRefresh();
            ui.ShowOsd($"Imported {added} feed(s).", 1200);
            return;
        }

        if (sub is "export")
        {
            string? path = argv.Length >= 2 ? argv[1] : null;
            if (string.IsNullOrWhiteSpace(path))
                path = OpmlIo.GetDefaultExportPath(baseName: "podliner-feeds.opml");

            string xml;
            try { xml = OpmlExporter.BuildXml(data.Feeds, "podliner feeds"); }
            catch (Exception ex) { ui.ShowOsd($"export: build error ({ex.Message})", 2000); return; }

            try
            {
                var used = OpmlIo.WriteFile(path!, xml, sanitizeFileNameIfNeeded: true, overwrite: true);
                ui.ShowOsd($"Exported → {used}", 1600);
            }
            catch (Exception ex) { ui.ShowOsd($"export: write error ({ex.Message})", 2000); }
            return;
        }

        ui.ShowOsd("usage: :opml import <path> [--update-titles] | :opml export [<path>]");
    }
}
