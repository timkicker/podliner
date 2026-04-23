using Serilog;
using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.App.Services;
using StuiPodcast.Infra.Opml;
using Terminal.Gui;

namespace StuiPodcast.App.Command;

// Replays the CLI flags (:net offline, :engine, :opml import/export, :feed,
// :search) after the UI is up. Uses the real command dispatcher so CLI and
// interactive input share the same code path.
static class CmdApplier
{
    public static void ApplyPostUiFlags(
        CliEntrypoint.Options cli,
        IUiShell? ui,
        AppData data,
        SwappableAudioPlayer? audioPlayer,
        PlaybackCoordinator? playback,
        MemoryLogSink memLog,
        Func<Task> save,
        DownloadManager? downloader,
        Func<AudioEngine, Task> engineSwitch,
        IEpisodeStore episodes,
        IFeedStore feedStore,
        IQueueService queue,
        CmdCases cases,
        GpodderSyncService? syncService = null)
    {
        Application.MainLoop?.Invoke(() =>
        {
            try
            {
                if (ui == null || audioPlayer == null || playback == null || downloader == null) return;

                void Dispatch(string raw) =>
                    CmdRouter.Handle(raw, audioPlayer, playback, ui, memLog, data, save, downloader,
                        episodes, feedStore, queue, cases, engineSwitch, syncService);

                if (cli.Offline)
                    Dispatch(":net offline");

                if (!string.IsNullOrWhiteSpace(cli.Engine))
                    Dispatch($":engine {cli.Engine}");

                if (!string.IsNullOrWhiteSpace(cli.OpmlExport))
                {
                    var path = cli.OpmlExport!;
                    Log.Information("cli/opml export path={Path}", path);
                    Dispatch($":opml export {path}");
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
                            var plan = OpmlImportPlanner.Plan(doc, feedStore.Snapshot(), updateTitles: false);
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
                            foreach (var f in feedStore.Snapshot().ToList())
                            {
                                episodes.RemoveByFeed(f.Id);
                                feedStore.Remove(f.Id);
                            }
                            data.LastSelectedFeedId = ui.AllFeedId;
                            _ = save();

                            ui.SetFeeds(feedStore.Snapshot(), data.LastSelectedFeedId);
                            cases.View.ApplyList();
                        }

                        var path = cli.OpmlImport!.Contains(' ') ? $"\"{cli.OpmlImport}\"" : cli.OpmlImport!;
                        Dispatch($":opml import {path}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(cli.Feed))
                {
                    var f = cli.Feed!.Trim();
                    Dispatch($":feed {f}");
                }

                if (!string.IsNullOrWhiteSpace(cli.Search))
                {
                    Dispatch($":search {cli.Search}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "cli/apply-post-ui failed engine={Engine} feed={Feed} opmlImport={OpmlIn} opmlExport={OpmlOut} search={Search}",
                    cli.Engine, cli.Feed, cli.OpmlImport, cli.OpmlExport, cli.Search);
            }
        });
    }
}
