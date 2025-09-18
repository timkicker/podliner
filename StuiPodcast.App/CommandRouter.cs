using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using StuiPodcast.App.Debug;
using StuiPodcast.Core;
using StuiPodcast.Infra;

static class CommandRouter
{
    // UI-Filter (wird jetzt persistiert, sobald du willst – aktuell nur in-memory)
    static bool _unplayedOnly = false;

    public static void Handle(string raw,
                              IPlayer player,
                              PlaybackCoordinator playback,
                              Shell ui,
                              MemoryLogSink mem,
                              AppData data,
                              Func<Task> persist) // <— SaveAsync aus Program
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var cmd = raw.Trim();

        // --- quick commands via keys ---
        if (cmd.Equals(":toggle", StringComparison.OrdinalIgnoreCase))
        {
            player.TogglePause();
            ui.UpdatePlayerUI(player.State);
            return;
        }
        
        // CommandRouter.cs – in Handle(...)
        if (cmd.StartsWith(":player", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd[7..].Trim().ToLowerInvariant();
            if (arg == "toggle" || string.IsNullOrEmpty(arg))
                ui.TogglePlayerPlacement();
            return;
        }

        
        if (cmd.StartsWith(":seek",  StringComparison.OrdinalIgnoreCase)) { Seek(cmd[5..].Trim(), player); return; }
        if (cmd.StartsWith(":vol",   StringComparison.OrdinalIgnoreCase)) { Volume(cmd[4..].Trim(), player, data, persist); return; }
        if (cmd.StartsWith(":speed", StringComparison.OrdinalIgnoreCase)) { Speed(cmd[6..].Trim(), player, data, persist); return; }
        if (cmd.StartsWith(":logs",  StringComparison.OrdinalIgnoreCase)) { Logs(cmd[5..].Trim(), ui); return; }
        if (cmd is ":h" or ":help") { ui.ShowKeysHelp(); return; }
        if (cmd is ":q" or ":quit") { ui.RequestQuit(); return; }

        // --- view / player placement ---
        if (cmd.StartsWith(":player", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd[7..].Trim().ToLowerInvariant();
            if (arg is "toggle" or "" or null)
            {
                ui.TogglePlayerPlacement();
                data.PlayerAtTop = !data.PlayerAtTop;
                _ = persist();
                return;
            }
            if (arg is "top")
            {
                ui.SetPlayerPlacement(true);
                data.PlayerAtTop = true;
                _ = persist();
                return;
            }
            if (arg is "bottom" or "bot")
            {
                ui.SetPlayerPlacement(false);
                data.PlayerAtTop = false;
                _ = persist();
                return;
            }
            return;
        }

        // --- filtering / navigation ---
        if (cmd.StartsWith(":filter", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd[7..].Trim().ToLowerInvariant();
            if (arg is "unplayed" or "only") _unplayedOnly = true;
            else if (arg is "all") _unplayedOnly = false;
            else if (arg is "toggle" or "" or null) _unplayedOnly = !_unplayedOnly;

            ApplyList(ui, data);
            return;
        }

        if (cmd.Equals(":next-unplayed", StringComparison.OrdinalIgnoreCase)) { JumpUnplayed(+1, ui, playback, data); return; }
        if (cmd.Equals(":prev-unplayed", StringComparison.OrdinalIgnoreCase)) { JumpUnplayed(-1, ui, playback, data); return; }

        // --- add/refresh (nur Bequemlichkeit; Hauptfluss hängt an Program/Shell) ---
        if (cmd.StartsWith(":add ", StringComparison.OrdinalIgnoreCase)) { ui.RequestAddFeed(cmd[5..].Trim()); return; }
        if (cmd.StartsWith(":refresh", StringComparison.OrdinalIgnoreCase)) { ui.RequestRefresh(); return; }

        // unknown → ignore
    }

    // ---- helpers ------------------------------------------------------------

    static void ApplyList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        var list = data.Episodes.Where(e => e.FeedId == feedId);
        if (_unplayedOnly) list = list.Where(e => !e.Played);

        if (feedId is Guid fid)
            ui.SetEpisodesForFeed(fid, list);
    }

    static void JumpUnplayed(int dir, Shell ui, PlaybackCoordinator playback, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        var eps = data.Episodes
            .Where(e => e.FeedId == feedId)
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();

        if (eps.Count == 0) return;

        var cur = ui.GetSelectedEpisode();
        var startIdx = cur is null ? -1 : eps.FindIndex(x => ReferenceEquals(x, cur) || x.Id == cur.Id);
        int i = startIdx;

        for (int step = 0; step < eps.Count; step++)
        {
            i = dir > 0
                ? (i + 1 + eps.Count) % eps.Count
                : (i - 1 + eps.Count) % eps.Count;

            if (!eps[i].Played)
            {
                var target = eps[i];
                playback.Play(target);
                ui.SetWindowTitle(target.Title);
                ui.ShowDetails(target);
                return;
            }
        }
    }

    static void Seek(string arg, IPlayer player)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;

        var s = player.State;
        var len = s.Length ?? TimeSpan.Zero;

        // percentage
        if (arg.EndsWith("%", StringComparison.Ordinal) &&
            double.TryParse(arg.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            if (len > TimeSpan.Zero)
            {
                var ms = Math.Clamp(pct / 100.0, 0, 1) * len.TotalMilliseconds;
                player.SeekTo(TimeSpan.FromMilliseconds(ms));
            }
            return;
        }

        // relative seconds +/-
        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relSecs))
        {
            player.SeekRelative(TimeSpan.FromSeconds(relSecs));
            return;
        }

        // mm:ss
        var parts = arg.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var mm) &&
            int.TryParse(parts[1], out var ss))
        {
            player.SeekTo(TimeSpan.FromSeconds(mm * 60 + ss));
            return;
        }

        // absolute seconds
        if (int.TryParse(arg, out var absSecs))
        {
            player.SeekTo(TimeSpan.FromSeconds(absSecs));
        }
    }

    static void Volume(string arg, IPlayer player, AppData data, Func<Task> persist)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Volume0_100;

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            var v = Math.Clamp(cur + delta, 0, 100);
            player.SetVolume(v);
            data.Volume0_100 = v;
            _ = persist();
            return;
        }
        if (int.TryParse(arg, out var abs))
        {
            var v = Math.Clamp(abs, 0, 100);
            player.SetVolume(v);
            data.Volume0_100 = v;
            _ = persist();
        }
    }

    static void Speed(string arg, IPlayer player, AppData data, Func<Task> persist)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Speed;

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
        {
            var s = Math.Clamp(cur + delta, 0.25, 3.0);
            player.SetSpeed(s);
            data.Speed = s;
            _ = persist();
            return;
        }
        if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var abs))
        {
            var s = Math.Clamp(abs, 0.25, 3.0);
            player.SetSpeed(s);
            data.Speed = s;
            _ = persist();
        }
    }

    static void Logs(string arg, Shell ui)
    {
        int tail = 500;
        if (int.TryParse(arg, out var n) && n > 0) tail = Math.Min(n, 5000);
        ui.ShowLogsOverlay(tail);
    }
}
