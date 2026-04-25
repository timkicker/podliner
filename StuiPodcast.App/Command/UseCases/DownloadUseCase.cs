using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;

namespace StuiPodcast.App.Command.UseCases;

// Handles :dl / :downloads sub-commands. Dispatches on top-level verb so the
// command router can fastpath without re-parsing. DlToggle is the UI-level
// shortcut used by the `d` key binding (toggle the selected episode's
// download state).
internal sealed class DownloadUseCase
{
    readonly IUiShell _ui;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;
    readonly DownloadManager _dlm;
    readonly ViewUseCase _view;
    readonly AppData? _data;

    public DownloadUseCase(IUiShell ui, Func<Task> persist, IEpisodeStore episodes, DownloadManager dlm, ViewUseCase view, AppData? data = null)
    {
        _ui = ui;
        _persist = persist;
        _episodes = episodes;
        _dlm = dlm;
        _view = view;
        _data = data;
    }

    public bool Handle(string cmd)
    {
        cmd = (cmd ?? "").Trim();

        if (cmd.StartsWith(":downloads", StringComparison.OrdinalIgnoreCase))
        {
            var dparts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sub = dparts.Length > 1 ? dparts[1].ToLowerInvariant() : "";

            if (string.IsNullOrEmpty(sub))
            {
                var q = _dlm.QueuedCount();
                var running = _dlm.CountInState(DownloadState.Running);
                var failed  = _dlm.CountInState(DownloadState.Failed);
                _ui.ShowOsd($"downloads: queue {q}, running {running}, failed {failed}", 1500);
                return true;
            }

            if (sub == "retry-failed")
            {
                int n = 0;
                foreach (var kv in _dlm.SnapshotMap())
                {
                    if (kv.Value.State == DownloadState.Failed) { _dlm.Enqueue(kv.Key); n++; }
                }
                _ = _persist();
                _ui.RefreshEpisodesForSelectedFeed(_episodes.Snapshot());
                _ui.ShowOsd($"downloads: retried {n} failed", 1500);
                return true;
            }

            if (sub == "clear-queue")
            {
                var n = _dlm.ClearQueue();
                _ = _persist();
                _ui.RefreshEpisodesForSelectedFeed(_episodes.Snapshot());
                _ui.ShowOsd($"downloads: cleared queue ({n})", 1200);
                return true;
            }

            if (sub == "open-dir")
            {
                var dir = GuessDownloadDir(_dlm);
                if (!string.IsNullOrWhiteSpace(dir)) TryOpenSystem(dir);
                else _ui.ShowOsd("downloads: no directory found", 1200);
                return true;
            }

            if (sub == "set-dir")
            {
                if (_data == null) { _ui.ShowOsd("downloads: set-dir not available here", 1500); return true; }

                // Everything after "set-dir" is the path. Quotes optional.
                var rest = cmd.Substring(":downloads".Length).TrimStart();
                if (rest.StartsWith("set-dir", StringComparison.OrdinalIgnoreCase))
                    rest = rest.Substring("set-dir".Length).Trim();
                rest = rest.Trim('"', '\'');

                if (rest.Length == 0)
                {
                    var current = _data.DownloadDir;
                    _ui.ShowOsd(current is null
                        ? "downloads dir: (default ~/Podcasts) — usage: :downloads set-dir <path|reset>"
                        : $"downloads dir: {current} — usage: :downloads set-dir <path|reset>", 2500);
                    return true;
                }

                if (rest.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                    rest.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                    rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    _data.DownloadDir = null;
                    _ = _persist();
                    _ui.ShowOsd("downloads dir: reset to default", 1500);
                    return true;
                }

                // Tilde + relative paths to absolute. Basic checks only — we
                // don't try to mkdir here, the next download will create the
                // dir on demand and surface any failure via Serilog.
                var expanded = ExpandPath(rest);
                _data.DownloadDir = expanded;
                _ = _persist();
                _ui.ShowOsd($"downloads dir: {expanded}", 2000);
                return true;
            }

            _ui.ShowOsd("downloads: retry-failed | clear-queue | open-dir | set-dir <path>", 1500);
            return true;
        }

        if (!cmd.StartsWith(":dl", StringComparison.OrdinalIgnoreCase)
            && !cmd.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
            return false;

        var ep = _ui.GetSelectedEpisode();
        if (ep == null) return true;

        var dlParts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var arg = dlParts.Length > 1 ? dlParts[1].ToLowerInvariant() : "";

        switch (arg)
        {
            case "start":
                _dlm.ForceFront(ep.Id);
                _dlm.EnsureRunning();
                _ui.ShowOsd("Downloading ⇣ (forced)");
                break;

            case "cancel":
                _dlm.Forget(ep.Id);
                _ui.ShowOsd("Download canceled ✖");
                break;

            default:
            {
                var st = _dlm.GetState(ep.Id);
                if (st == DownloadState.None || st == DownloadState.Canceled || st == DownloadState.Failed)
                {
                    _dlm.Enqueue(ep.Id);
                    _dlm.EnsureRunning();
                    _ui.ShowOsd("Download queued ⌵");
                }
                else
                {
                    _dlm.Forget(ep.Id);
                    _ui.ShowOsd("Download unqueued");
                }
                break;
            }
        }

        _ = _persist();
        _ui.RefreshEpisodesForSelectedFeed(_episodes.Snapshot());
        return true;
    }

    public void DlToggle(string arg)
    {
        var ep = _ui.GetSelectedEpisode();
        if (ep is null) return;

        bool wantOn;
        if (arg is "on" or "true" or "+") wantOn = true;
        else if (arg is "off" or "false" or "-") wantOn = false;
        else wantOn = !Program.IsDownloaded(ep.Id);

        if (wantOn)
        {
            _dlm.Enqueue(ep.Id);
            _dlm.EnsureRunning();
            _ui.ShowOsd("Download queued ⌵");
        }
        else
        {
            _dlm.Forget(ep.Id);
            _ui.ShowOsd("Download canceled ✖");
        }

        _ = _persist();
        _view.ApplyList();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static string? GuessDownloadDir(DownloadManager dlm)
    {
        try
        {
            var any = dlm.SnapshotMap()
                .Where(kv => kv.Value.State == DownloadState.Done && !string.IsNullOrWhiteSpace(kv.Value.LocalPath))
                .Select(kv => Path.GetDirectoryName(kv.Value.LocalPath!)!)
                .FirstOrDefault(p => p != null && Directory.Exists(p));
            return any;
        }
        catch { return null; }
    }

    // Expands "~" and resolves relative paths against the user's home so
    // typing `:downloads set-dir ~/podcasts` does what the user expects.
    static string ExpandPath(string raw)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var p = raw;
        if (p == "~") p = home;
        else if (p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Path.Combine(home, p.Substring(2));
        return Path.IsPathRooted(p) ? p : Path.GetFullPath(p);
    }

    static bool TryOpenSystem(string url)
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
