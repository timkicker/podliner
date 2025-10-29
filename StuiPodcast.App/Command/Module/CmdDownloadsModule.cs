using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;

namespace StuiPodcast.App.Command.Module;

internal static class CmdDownloadsModule
{
    public static bool HandleDownloads(string cmd, UiShell ui, AppData data, DownloadManager dlm, Func<Task> saveAsync)
    {
        cmd = (cmd ?? "").Trim();

        if (cmd.StartsWith(":downloads", StringComparison.OrdinalIgnoreCase))
        {
            var dparts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sub = dparts.Length > 1 ? dparts[1].ToLowerInvariant() : "";

            if (string.IsNullOrEmpty(sub))
            {
                var q = data.DownloadQueue.Count;
                var running = data.DownloadMap.Count(kv => kv.Value.State == DownloadState.Running);
                var failed = data.DownloadMap.Count(kv => kv.Value.State == DownloadState.Failed);
                ui.ShowOsd($"downloads: queue {q}, running {running}, failed {failed}", 1500);
                return true;
            }

            if (sub == "retry-failed")
            {
                int n = 0;
                foreach (var (id, st) in data.DownloadMap.ToArray())
                {
                    if (st.State == DownloadState.Failed) { dlm.Enqueue(id); n++; }
                }
                _ = saveAsync();
                ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                ui.ShowOsd($"downloads: retried {n} failed", 1500);
                return true;
            }

            if (sub == "clear-queue")
            {
                int n = data.DownloadQueue.Count;
                data.DownloadQueue.Clear();
                _ = saveAsync();
                ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                ui.ShowOsd($"downloads: cleared queue ({n})", 1200);
                return true;
            }

            if (sub == "open-dir")
            {
                var dir = GuessDownloadDir(data);
                if (!string.IsNullOrWhiteSpace(dir)) TryOpenSystem(dir);
                else ui.ShowOsd("downloads: no directory found", 1200);
                return true;
            }

            ui.ShowOsd("downloads: retry-failed | clear-queue | open-dir", 1200);
            return true;
        }

        if (!cmd.StartsWith(":dl", StringComparison.OrdinalIgnoreCase)
            && !cmd.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
            return false;

        var ep = ui.GetSelectedEpisode();
        if (ep == null) return true;

        var dlParts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var arg = dlParts.Length > 1 ? dlParts[1].ToLowerInvariant() : "";

        switch (arg)
        {
            case "start":
                dlm.ForceFront(ep.Id);
                dlm.EnsureRunning();
                ui.ShowOsd("Downloading ⇣ (forced)");
                break;

            case "cancel":
                dlm.Cancel(ep.Id);
                data.DownloadMap.Remove(ep.Id);
                data.DownloadQueue.RemoveAll(x => x == ep.Id);
                ui.ShowOsd("Download canceled ✖");
                break;

            default:
            {
                var st = dlm.GetState(ep.Id);
                if (st == DownloadState.None || st == DownloadState.Canceled || st == DownloadState.Failed)
                {
                    dlm.Enqueue(ep.Id);
                    dlm.EnsureRunning();         // <— WICHTIG: starten!
                    ui.ShowOsd("Download queued ⌵");
                }
                else
                {
                    dlm.Cancel(ep.Id);
                    data.DownloadMap.Remove(ep.Id);
                    data.DownloadQueue.RemoveAll(x => x == ep.Id);
                    ui.ShowOsd("Download unqueued");
                }
                break;
            }

        }

        _ = saveAsync();
        ui.RefreshEpisodesForSelectedFeed(data.Episodes);
        return true;
    }

    public static void DlToggle(string arg, UiShell ui, AppData data, Func<Task> persist, DownloadManager dlm)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool wantOn;
        if (arg is "on" or "true" or "+") wantOn = true;
        else if (arg is "off" or "false" or "-") wantOn = false;
        else wantOn = !Program.IsDownloaded(ep.Id);

        if (wantOn)
        {
            dlm.Enqueue(ep.Id);
            dlm.EnsureRunning();
            ui.ShowOsd("Download queued ⌵");
        }
        else
        {
            dlm.Cancel(ep.Id);
            data.DownloadQueue.RemoveAll(x => x == ep.Id);
            data.DownloadMap.Remove(ep.Id);
            ui.ShowOsd("Download canceled ✖");
        }

        _ = persist();
        CmdViewModule.ApplyList(ui, data);
    }

    private static string? GuessDownloadDir(AppData data)
    {
        try
        {
            var any = data.DownloadMap.Values
                .Where(v => v.State == DownloadState.Done && !string.IsNullOrWhiteSpace(v.LocalPath))
                .Select(v => Path.GetDirectoryName(v.LocalPath!)!)
                .FirstOrDefault(p => p != null && Directory.Exists(p));
            return any;
        }
        catch { return null; }
    }

    private static bool TryOpenSystem(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            { var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }; System.Diagnostics.Process.Start(psi); return true; }
            if (OperatingSystem.IsMacOS())
            { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", url) { UseShellExecute = false }); return true; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }
}
