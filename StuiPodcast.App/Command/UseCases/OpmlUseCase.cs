using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Infra.Opml;

namespace StuiPodcast.App.Command.UseCases;

// OPML import / export. Import plans the diff against the existing feed
// store and funnels each new feed through UiShell.RequestAddFeed so it
// hits the same pipeline as a manual :add (feed probe, persistence,
// refresh).
internal sealed class OpmlUseCase
{
    readonly IUiShell _ui;
    readonly Func<Task> _persist;
    readonly IFeedStore _feeds;

    public OpmlUseCase(IUiShell ui, Func<Task> persist, IFeedStore feeds)
    {
        _ui = ui;
        _persist = persist;
        _feeds = feeds;
    }

    public void ExecOpml(string[] args)
    {
        var argv = args ?? Array.Empty<string>();
        if (argv.Length == 0) { _ui.ShowOsd("usage: :opml import <path> [--update-titles] | :opml export [<path>]"); return; }

        var sub = argv[0].ToLowerInvariant();
        if (sub is "import")
        {
            if (argv.Length < 2) { _ui.ShowOsd("usage: :opml import <path> [--update-titles]"); return; }

            var path = argv[1];
            bool updateTitles = argv.Any(a => string.Equals(a, "--update-titles", StringComparison.OrdinalIgnoreCase));

            string xml;
            try { xml = OpmlIo.ReadFile(path); }
            catch (Exception ex) { _ui.ShowOsd($"import: read error ({ex.Message})", 2000); return; }

            OpmlDocument doc;
            try { doc = OpmlParser.Parse(xml); }
            catch (Exception ex) { _ui.ShowOsd($"import: parse error ({ex.Message})", 2000); return; }

            var plan = OpmlImportPlanner.Plan(doc, _feeds.Snapshot(), updateTitles);
            _ui.ShowOsd($"OPML: new {plan.NewCount}, dup {plan.DuplicateCount}, invalid {plan.InvalidCount}", 1600);

            if (plan.NewCount == 0) return;

            int added = 0;
            foreach (var item in plan.NewItems())
            {
                var url = item.Entry.XmlUrl?.Trim();
                if (string.IsNullOrWhiteSpace(url)) continue;

                _ui.RequestAddFeed(url);
                added++;
            }

            _ = _persist();
            _ui.RequestRefresh();
            _ui.ShowOsd($"Imported {added} feed(s).", 1200);
            return;
        }

        if (sub is "export")
        {
            string? path = argv.Length >= 2 ? argv[1] : null;
            if (string.IsNullOrWhiteSpace(path))
                path = OpmlIo.GetDefaultExportPath(baseName: "podliner-feeds.opml");

            string xml;
            try { xml = OpmlExporter.BuildXml(_feeds.Snapshot(), "podliner feeds"); }
            catch (Exception ex) { _ui.ShowOsd($"export: build error ({ex.Message})", 2000); return; }

            try
            {
                var used = OpmlIo.WriteFile(path!, xml, sanitizeFileNameIfNeeded: true, overwrite: true);
                _ui.ShowOsd($"Exported → {used}", 1600);
            }
            catch (Exception ex) { _ui.ShowOsd($"export: write error ({ex.Message})", 2000); }
            return;
        }

        _ui.ShowOsd("usage: :opml import <path> [--update-titles] | :opml export [<path>]");
    }
}
