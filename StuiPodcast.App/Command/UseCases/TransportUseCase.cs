using System.Globalization;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command.UseCases;

// Transport controls — seek, volume, speed, replay, now, jump. All feed
// through the player's capability flags so unsupported ops fail with an
// honest OSD instead of silent no-ops. The audio player is the swappable
// proxy so engine switches don't break the binding.
internal sealed class TransportUseCase
{
    readonly IAudioPlayer _audioPlayer;
    readonly IUiShell _ui;
    readonly AppData _data;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;

    public TransportUseCase(IAudioPlayer audioPlayer, IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes)
    {
        _audioPlayer = audioPlayer;
        _ui = ui;
        _data = data;
        _persist = persist;
        _episodes = episodes;
    }

    public void ExecSeek(string[] args)
    {
        if ((_audioPlayer.Capabilities & PlayerCapabilities.Seek) == 0) { _ui.ShowOsd("seek not supported by current engine"); return; }
        if (string.Equals(_audioPlayer.Name, "ffplay", StringComparison.OrdinalIgnoreCase)) _ui.ShowOsd("coarse seek (ffplay): restarts stream", 1100);
        Seek(string.Join(' ', args ?? Array.Empty<string>()).Trim());
    }

    public void ExecVolume(string[] args) => Volume(string.Join(' ', args ?? Array.Empty<string>()).Trim());
    public void ExecSpeed(string[] args)  => Speed(string.Join(' ', args ?? Array.Empty<string>()).Trim());
    public void ExecReplay(string[] args) => Replay(string.Join(' ', args ?? Array.Empty<string>()).Trim());

    public void ExecNow()
    {
        var nowId = _ui.GetNowPlayingId();
        if (nowId == null) { _ui.ShowOsd("no episode playing"); return; }
        var list = EpisodeListBuilder.BuildCurrentList(_ui, _data, _episodes);
        var idx = list.FindIndex(e => e.Id == nowId);
        if (idx < 0) { _ui.ShowOsd("playing episode not in current view"); return; }
        _ui.SelectEpisodeIndex(idx);
        _ui.ShowOsd("jumped to now", 700);
    }

    public void ExecJump(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (string.IsNullOrEmpty(arg)) { _ui.ShowOsd("usage: :jump <hh:mm[:ss]|+/-sec|%>"); return; }
        Seek(arg);
    }

    // ── primitives (kept public so tests can exercise parsing directly) ──────

    public void Replay(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) { _audioPlayer.SeekTo(TimeSpan.Zero); return; }
        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec > 0)
            _audioPlayer.SeekRelative(TimeSpan.FromSeconds(-sec));
        else
            _audioPlayer.SeekTo(TimeSpan.Zero);
    }

    public void Seek(string arg)
    {
        if ((_audioPlayer.Capabilities & PlayerCapabilities.Seek) == 0) return;
        if (string.IsNullOrWhiteSpace(arg)) return;

        var s = _audioPlayer.State;
        var len = s.Length ?? TimeSpan.Zero;

        if (arg.EndsWith("%", StringComparison.Ordinal) &&
            double.TryParse(arg.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            if (len > TimeSpan.Zero)
            {
                var ms = Math.Clamp(pct / 100.0, 0, 1) * len.TotalMilliseconds;
                _audioPlayer.SeekTo(TimeSpan.FromMilliseconds(ms));
            }
            return;
        }

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relSecs))
        {
            _audioPlayer.SeekRelative(TimeSpan.FromSeconds(relSecs));
            return;
        }

        var parts = arg.Split(':');
        if (parts.Length is 2 or 3)
        {
            int hh = 0, mm = 0, ss = 0;
            if (parts.Length == 3) { int.TryParse(parts[0], out hh); int.TryParse(parts[1], out mm); int.TryParse(parts[2], out ss); }
            else { int.TryParse(parts[0], out mm); int.TryParse(parts[1], out ss); }
            var total = hh * 3600 + mm * 60 + ss;
            _audioPlayer.SeekTo(TimeSpan.FromSeconds(Math.Max(0, total)));
            return;
        }

        if (int.TryParse(arg, out var absSecs))
            _audioPlayer.SeekTo(TimeSpan.FromSeconds(absSecs));
    }

    public void Volume(string arg)
    {
        if ((_audioPlayer.Capabilities & PlayerCapabilities.Volume) == 0) { _ui.ShowOsd("volume not supported on this engine"); return; }
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = _audioPlayer.State.Volume0_100;

        if ((arg.StartsWith("+") || arg.StartsWith("-")) && int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            var v = Math.Clamp(cur + delta, 0, 100);
            _audioPlayer.SetVolume(v); _data.Volume0_100 = v; _ = _persist(); _ui.ShowOsd($"Vol {v}%"); return;
        }
        if (int.TryParse(arg, out var abs))
        { var v = Math.Clamp(abs, 0, 100); _audioPlayer.SetVolume(v); _data.Volume0_100 = v; _ = _persist(); _ui.ShowOsd($"Vol {v}%"); }
    }

    public void Speed(string arg)
    {
        if ((_audioPlayer.Capabilities & PlayerCapabilities.Speed) == 0) { _ui.ShowOsd("speed not supported by current engine"); return; }
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = _audioPlayer.State.Speed;

        arg = arg.Replace(',', '.');

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
        {
            var s2 = Math.Clamp(cur + delta, 0.25, 3.0);
            _audioPlayer.SetSpeed(s2); _data.Speed = s2; _ = _persist(); _ui.ShowOsd($"Speed {s2:0.0}×"); return;
        }
        if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var abs))
        {
            var s2 = Math.Clamp(abs, 0.25, 3.0);
            _audioPlayer.SetSpeed(s2); _data.Speed = s2; _ = _persist(); _ui.ShowOsd($"Speed {s2:0.0}×");
        }
    }
}
