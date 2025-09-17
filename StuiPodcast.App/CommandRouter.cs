using System;
using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.Infra;

static class CommandRouter
{
    public static void Handle(string cmd, IPlayer player, PlaybackCoordinator playback, Shell ui, MemoryLogSink mem)
    {
        try
        {
            if (cmd.StartsWith(":add "))
            {
                ui.RequestAddFeed(cmd[5..].Trim());
            }
            else if (cmd.StartsWith(":refresh"))
            {
                ui.RequestRefresh();
            }
            else if (cmd is ":q" or ":quit")
            {
                ui.RequestQuit();
            }
            else if (cmd is ":h" or ":help")
            {
                ui.ShowKeysHelp();
            }
            else if (cmd.StartsWith(":logs"))
            {
                var arg = cmd.Length > 5 ? cmd[5..].Trim() : "";
                int tail = 500;
                if (int.TryParse(arg, out var n) && n > 0) tail = Math.Min(n, 5000);
                ui.ShowLogsOverlay(tail);
            }
            else if (cmd.StartsWith(":seek"))
            {
                var arg = cmd[5..].Trim();
                if (string.IsNullOrWhiteSpace(arg)) return;

                if (arg.EndsWith("%") && double.TryParse(arg.TrimEnd('%'), out var pct))
                {
                    if (player.State.Length is TimeSpan len)
                    {
                        var pos = TimeSpan.FromMilliseconds(len.TotalMilliseconds * Math.Clamp(pct / 100.0, 0, 1));
                        player.SeekTo(pos);
                    }
                    return;
                }

                if (arg.StartsWith("+") || arg.StartsWith("-"))
                {
                    if (int.TryParse(arg, out var secsRel))
                        player.SeekRelative(TimeSpan.FromSeconds(secsRel));
                    return;
                }

                var parts = arg.Split(':');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var mm) &&
                    int.TryParse(parts[1], out var ss))
                {
                    player.SeekTo(TimeSpan.FromSeconds(mm * 60 + ss));
                }
            }
            else if (cmd.StartsWith(":vol"))
            {
                var arg = cmd[4..].Trim();
                if (int.TryParse(arg, out var v))
                    player.SetVolume(v);
            }
            else if (cmd.StartsWith(":speed"))
            {
                var arg = cmd[6..].Trim();
                if (double.TryParse(arg, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var sp))
                    player.SetSpeed(sp);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Command error");
            ui.ShowError("Command error", ex.Message);
        }
    }
}
