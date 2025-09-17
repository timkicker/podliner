using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Terminal.Gui;
using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.App.Debug;

static class CommandRouter
{
    public static async Task HandleAsync(
        string cmd,
        AppData data,
        FeedService feeds,
        IPlayer player,
        PlaybackCoordinator playback,
        Shell ui,
        Func<Task> save,
        MemoryLogSink mem // handy if you want to log into the in-app log
    )
    {
        if (string.IsNullOrWhiteSpace(cmd)) return;
        cmd = cmd.Trim();

        // quick exit
        if (cmd is ":q" or ":quit")
        {
            ui.RequestQuit();
            return;
        }

        // help / logs / theme are UI-only and cheap
        if (cmd is ":h" or ":help") { ui.ShowKeysHelp(); return; }
        if (cmd.StartsWith(":logs"))
        {
            var tail = 500;
            var arg = cmd.Length > 5 ? cmd[5..].Trim() : "";
            if (int.TryParse(arg, out var n) && n > 0) tail = Math.Min(n, 5000);
            ui.ShowLogsOverlay(tail);
            return;
        }
        if (cmd is ":theme") { ui.ToggleTheme(); return; }

        // playback controls (no feed service needed)
        if (cmd == ":toggle") { player.TogglePause(); return; }

        if (cmd.StartsWith(":seek"))
        {
            var arg = cmd[5..].Trim();
            if (string.IsNullOrEmpty(arg)) return;

            // 100%
            if (arg.EndsWith("%") && double.TryParse(arg.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                if (player.State.Length is TimeSpan len)
                {
                    var pos = TimeSpan.FromMilliseconds(len.TotalMilliseconds * Math.Clamp(pct / 100.0, 0, 1));
                    player.SeekTo(pos);
                }
                return;
            }

            // relative +/-
            if ((arg.StartsWith("+") || arg.StartsWith("-")) && int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dSecs))
            {
                player.SeekRelative(TimeSpan.FromSeconds(dSecs));
                return;
            }

            // mm:ss
            var parts = arg.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var mm)
                && int.TryParse(parts[1], out var ss))
            {
                player.SeekTo(TimeSpan.FromSeconds(mm * 60 + ss));
                return;
            }
            return;
        }

        if (cmd.StartsWith(":vol"))
        {
            var arg = cmd[4..].Trim();
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return;

            if (arg.StartsWith("+") || arg.StartsWith("-"))
                player.SetVolume(player.State.Volume0_100 + v);
            else
                player.SetVolume(v);

            return;
        }

        if (cmd.StartsWith(":speed"))
        {
            var arg = cmd[6..].Trim();
            if (!double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return;

            if (arg.StartsWith("+") || arg.StartsWith("-"))
                player.SetSpeed(player.State.Speed + s);
            else
                player.SetSpeed(s);

            return;
        }

        if (cmd is ":mark")
        {
            var ep = ui.GetSelectedEpisode();
            if (ep == null) return;

            ep.Played = !ep.Played;
            if (ep.Played && ep.LengthMs is long len) ep.LastPosMs = len;

            await save();
            var feedId = ui.GetSelectedFeedId();
            if (feedId != null) ui.SetEpisodesForFeed(feedId.Value, data.Episodes);
            if (ep != null) ui.ShowDetails(ep);
            return;
        }

        if (cmd.StartsWith(":search"))
        {
            var q = cmd.Length > 8 ? cmd[8..].Trim() : "";
            var feedId = ui.GetSelectedFeedId();
            if (feedId == null) return;

            var list = data.Episodes.Where(e => e.FeedId == feedId.Value);
            if (!string.IsNullOrWhiteSpace(q))
            {
                list = list.Where(e =>
                    (e.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.DescriptionText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            ui.SetEpisodesForFeed(feedId.Value, list);
            return;
        }

        if (cmd.StartsWith(":add "))
        {
            var url = cmd[5..].Trim();
            if (string.IsNullOrWhiteSpace(url)) return;

            var f = await feeds.AddFeedAsync(url);
            ui.SetFeeds(data.Feeds, f.Id);
            ui.SetEpisodesForFeed(f.Id, data.Episodes);
            await save();
            return;
        }

        if (cmd is ":refresh")
        {
            await feeds.RefreshAllAsync();
            ui.SetFeeds(data.Feeds);
            var feedId = ui.GetSelectedFeedId();
            if (feedId != null) ui.SetEpisodesForFeed(feedId.Value, data.Episodes);
            await save();
            return;
        }

        // unknown → ignore quietly
    }
}
