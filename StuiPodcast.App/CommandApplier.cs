using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Opml;
using Terminal.Gui;

namespace StuiPodcast.App;

// ==========================================================
// Post-UI CLI flags applier
// ==========================================================
static class CommandApplier
{
    public static void ApplyPostUiFlags(
        Cli.Options cli,
        Shell ui,
        AppData data,
        SwappablePlayer player,
        PlaybackCoordinator playback,
        MemoryLogSink memLog,
        Func<Task> save,
        DownloadManager downloader,
        Func<string, Task> engineSwitch)
    {
        Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui == null || player == null || playback == null || downloader == null) return;

                if (cli.Offline)
                    CommandRouter.Handle(":net offline", player, playback, ui, memLog, data, save, downloader, engineSwitch);

                if (!string.IsNullOrWhiteSpace(cli.Engine))
                    CommandRouter.Handle($":engine {cli.Engine}", player, playback, ui, memLog, data, save, downloader, engineSwitch);

                if (!string.IsNullOrWhiteSpace(cli.OpmlExport))
                {
                    var path = cli.OpmlExport!;
                    Log.Information("cli/opml export path={Path}", path);
                    CommandRouter.Handle($":opml export {path}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                }

                if (!string.IsNullOrWhiteSpace(cli.OpmlImport))
                {
                    var mode = (cli.OpmlImportMode ?? "merge").Trim().ToLowerInvariant();

                    if (mode == "dry-run")
                    {
                        try
                        {
                            var xml = OpmlIo.ReadFile(cli.OpmlImport!);
                            var doc = OpmlParser.Parse(xml);
                            var plan = OpmlImportPlanner.Plan(doc, data.Feeds, updateTitles: false);
                            Log.Information("cli/opml dryrun path={Path} new={New} dup={Dup} invalid={Invalid}",
                                cli.OpmlImport, plan.NewCount, plan.DuplicateCount, plan.InvalidCount);
                            ui.ShowOsd($"OPML dry-run → new {plan.NewCount}, dup {plan.DuplicateCount}, invalid {plan.InvalidCount}", 2400);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "cli/opml dryrun failed path={Path}", cli.OpmlImport);
                            ui.ShowOsd($"OPML dry-run failed: {ex.Message}", 2000);
                        }
                    }
                    else
                    {
                        Log.Information("cli/opml import path={Path} mode={Mode}", cli.OpmlImport, mode);
                        if (mode == "replace")
                        {
                            Log.Information("cli/opml replace clearing existing feeds/episodes");
                            data.Feeds.Clear();
                            data.Episodes.Clear();
                            data.LastSelectedFeedId = ui.AllFeedId;
                            _ = save();

                            ui.SetFeeds(data.Feeds, data.LastSelectedFeedId);
                            CommandRouter.ApplyList(ui, data);
                        }

                        var path = cli.OpmlImport!.Contains(' ') ? $"\"{cli.OpmlImport}\"" : cli.OpmlImport!;
                        CommandRouter.Handle($":opml import {path}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                    }
                }

                if (!string.IsNullOrWhiteSpace(cli.Feed))
                {
                    var f = cli.Feed!.Trim();
                    CommandRouter.Handle($":feed {f}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                }

                if (!string.IsNullOrWhiteSpace(cli.Search))
                {
                    CommandRouter.Handle($":search {cli.Search}", player, playback, ui, memLog, data, save, downloader, engineSwitch);
                }
            }
            catch { }
        });
    }
}