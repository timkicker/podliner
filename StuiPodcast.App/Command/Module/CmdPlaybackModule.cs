using System.Globalization;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command.Module;

internal static class CmdPlaybackModule
{
    public static void ExecSeek(string[] args, IPlayer player, UiShell ui)
    {
        if ((player.Capabilities & PlayerCapabilities.Seek) == 0) { ui.ShowOsd("seek not supported by current engine"); return; }
        if (string.Equals(player.Name, "ffplay", StringComparison.OrdinalIgnoreCase)) ui.ShowOsd("coarse seek (ffplay): restarts stream", 1100);
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Seek(arg, player);
    }

    public static void ExecVolume(string[] args, IPlayer player, AppData data, Func<Task> persist, UiShell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Volume(arg, player, data, persist, ui);
    }

    public static void ExecSpeed(string[] args, IPlayer player, AppData data, Func<Task> persist, UiShell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Speed(arg, player, data, persist, ui);
    }

    public static void ExecReplay(string[] args, IPlayer player, UiShell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Replay(arg, player, ui);
    }

    public static void ExecNow(UiShell ui, AppData data)
    {
        var nowId = ui.GetNowPlayingId();
        if (nowId == null) { ui.ShowOsd("no episode playing"); return; }
        var list = EpisodeListBuilder.BuildCurrentList(ui, data);
        var idx = list.FindIndex(e => e.Id == nowId);
        if (idx < 0) { ui.ShowOsd("playing episode not in current view"); return; }
        ui.SelectEpisodeIndex(idx);
        ui.ShowOsd("jumped to now", 700);
    }

    public static void ExecJump(string[] args, IPlayer player, UiShell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (string.IsNullOrEmpty(arg)) { ui.ShowOsd("usage: :jump <hh:mm[:ss]|+/-sec|%>"); return; }
        Seek(arg, player);
    }

    // --- helpers copied
    public static void Replay(string arg, IPlayer player, UiShell ui)
    {
        if (string.IsNullOrWhiteSpace(arg)) { player.SeekTo(TimeSpan.Zero); return; }
        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec > 0)
            player.SeekRelative(TimeSpan.FromSeconds(-sec));
        else
            player.SeekTo(TimeSpan.Zero);
    }

    public static void Seek(string arg, IPlayer player)
    {
        if ((player.Capabilities & PlayerCapabilities.Seek) == 0) return;
        if (string.IsNullOrWhiteSpace(arg)) return;

        var s = player.State;
        var len = s.Length ?? TimeSpan.Zero;

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

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relSecs))
        {
            player.SeekRelative(TimeSpan.FromSeconds(relSecs));
            return;
        }

        var parts = arg.Split(':');
        if (parts.Length is 2 or 3)
        {
            int hh = 0, mm = 0, ss = 0;
            if (parts.Length == 3) { int.TryParse(parts[0], out hh); int.TryParse(parts[1], out mm); int.TryParse(parts[2], out ss); }
            else { int.TryParse(parts[0], out mm); int.TryParse(parts[1], out ss); }
            var total = hh * 3600 + mm * 60 + ss;
            player.SeekTo(TimeSpan.FromSeconds(Math.Max(0, total)));
            return;
        }

        if (int.TryParse(arg, out var absSecs))
            player.SeekTo(TimeSpan.FromSeconds(absSecs));
    }

    public static void Volume(string arg, IPlayer player, AppData data, Func<Task> persist, UiShell ui)
    {
        if ((player.Capabilities & PlayerCapabilities.Volume) == 0) { ui.ShowOsd("volume not supported on this engine"); return; }
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Volume0_100;

        if ((arg.StartsWith("+") || arg.StartsWith("-")) && int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            var v = Math.Clamp(cur + delta, 0, 100);
            player.SetVolume(v); data.Volume0_100 = v; _ = persist(); ui.ShowOsd($"Vol {v}%"); return;
        }
        if (int.TryParse(arg, out var abs))
        { var v = Math.Clamp(abs, 0, 100); player.SetVolume(v); data.Volume0_100 = v; _ = persist(); ui.ShowOsd($"Vol {v}%"); }
    }

    public static void Speed(string arg, IPlayer player, AppData data, Func<Task> persist, UiShell ui)
    {
        if ((player.Capabilities & PlayerCapabilities.Speed) == 0) { ui.ShowOsd("speed not supported on this engine"); return; }
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Speed;

        arg = arg.Replace(',', '.');

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
        {
            var s2 = Math.Clamp(cur + delta, 0.25, 3.0);
            player.SetSpeed(s2); data.Speed = s2; _ = persist(); ui.ShowOsd($"Speed {s2:0.0}×"); return;
        }
        if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var abs))
        {
            var s2 = Math.Clamp(abs, 0.25, 3.0);
            player.SetSpeed(s2); data.Speed = s2; _ = persist(); ui.ShowOsd($"Speed {s2:0.0}×");
        }
    }
}
