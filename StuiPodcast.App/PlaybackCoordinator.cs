using System;
using System.Linq;
using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.Core;
using StuiPodcast.Infra;

sealed class PlaybackCoordinator
{
    private readonly AppData _data;
    private readonly IPlayer _player;
    private readonly Func<System.Threading.Tasks.Task> _save;
    private readonly MemoryLogSink _mem;

    private Episode? _current;
    private DateTime _lastUiRefresh = DateTime.MinValue;
    private DateTime _lastPeriodicSave = DateTime.MinValue;

    public PlaybackCoordinator(AppData data, IPlayer player, Func<System.Threading.Tasks.Task> save, MemoryLogSink mem)
    {
        _data = data; _player = player; _save = save; _mem = mem;
    }

    public void Play(Episode ep)
    {
        if (_current != null && !ReferenceEquals(_current, ep))
            _ = _save();

        _current = ep;

        long? startMs = ep.LastPosMs;
        if (startMs is long ms && ep.LengthMs is long len &&
            (ms < 5_000 || ms > (len - 10_000)))
            startMs = null;

        _player.Play(ep.AudioUrl, startMs);
    }

    public void PersistProgressTick(PlayerState s,
        Action<System.Collections.Generic.IEnumerable<Episode>> refreshEpisodes,
        System.Collections.Generic.IEnumerable<Episode> allEpisodes)
    {
        if (_current == null) return;

        var lenMs = s.Length?.TotalMilliseconds ?? 0;
        var posMs = Math.Max(0, s.Position.TotalMilliseconds);

        if (lenMs > 0) _current.LengthMs = (long)lenMs;
        _current.LastPosMs = (long)posMs;

        if (lenMs > 0)
        {
            var remain = TimeSpan.FromMilliseconds(lenMs - posMs);
            var ratio  = posMs / lenMs;
            if (ratio >= 0.90 || remain <= TimeSpan.FromSeconds(30))
            {
                if (!_current.Played)
                {
                    _current.Played = true;
                    _current.LastPlayedAt = DateTimeOffset.Now;
                    Log.Information("Episode marked played: {Title}", _current.Title);
                    _ = _save();
                }
            }
        }

        if ((DateTime.UtcNow - _lastUiRefresh) > TimeSpan.FromSeconds(1))
        {
            _lastUiRefresh = DateTime.UtcNow;
            var feedId = _current.FeedId;
            refreshEpisodes(allEpisodes.Where(e => e.FeedId == feedId));
        }

        if ((DateTime.UtcNow - _lastPeriodicSave) > TimeSpan.FromSeconds(3))
        {
            _lastPeriodicSave = DateTime.UtcNow;
            _ = _save();
        }
    }
}
