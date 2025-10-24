using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;
using StuiPodcast.Infra.Player;

#region playback coordinator

// coordinates playback progress, persistence and auto advance
sealed class PlaybackCoordinator
{
    #region deps and state

    private readonly AppData _data;
    private readonly IAudioPlayer _audioPlayer;
    private readonly Func<Task> _saveAsync;
    private readonly MemoryLogSink _mem;

    private Episode? _current;

    private CancellationTokenSource? _resumeCts;
    private CancellationTokenSource? _stallCts;
    private bool _progressSeenForSession = false;

    private DateTime _lastUiRefresh    = DateTime.MinValue;
    private DateTime _lastPeriodicSave = DateTime.MinValue;
    private DateTimeOffset _lastAutoAdv = DateTimeOffset.MinValue;

    private bool _endHandledForSession = false;
    private int _sid = 0;

    public event Action<Episode>? AutoAdvanceSuggested;
    public event Action? QueueChanged;
    public event Action<PlaybackSnapshot>? SnapshotAvailable;
    public event Action<PlaybackStatus>? StatusChanged;

    private PlaybackSnapshot _lastSnapshot = PlaybackSnapshot.Empty;

    public PlaybackCoordinator(AppData data, IAudioPlayer audioPlayer, Func<Task> saveAsync, MemoryLogSink mem)
    {
        _data = data;
        _audioPlayer = audioPlayer;
        _saveAsync = saveAsync;
        _mem = mem;
    }

    #endregion

    #region public api

    public void Play(Episode ep)
    {
        unchecked { _sid++; }
        var sid = _sid;

        ConsumeQueueUpToInclusive(ep.Id);

        _current = ep;
        _endHandledForSession = false;
        _progressSeenForSession = false;

        _current.Progress.LastPlayedAt = DateTimeOffset.Now;
        _ = _saveAsync();

        CancelResume();
        CancelStallWatch();

        long? startMs = ep.Progress.LastPosMs;
        if (startMs is long ms)
        {
            long knownLen = ep.DurationMs;
            if ((knownLen > 0 && (ms < 5000 || ms > knownLen - 10000)) ||
                (knownLen == 0 && ms < 5000))
            {
                startMs = null;
            }
        }

        FireStatus(PlaybackStatus.Loading);
        _audioPlayer.Play(ep.AudioUrl, null);

        if (startMs is long want && want > 0)
            StartOneShotResume(want, sid);

        StartStallWatch(sid, TimeSpan.FromSeconds(5));

        _lastSnapshot = PlaybackSnapshot.From(
            sid,
            ep.Id,
            TimeSpan.Zero,
            _audioPlayer.State.Length ?? TimeSpan.Zero,
            _audioPlayer.State.IsPlaying,
            _audioPlayer.State.Speed,
            DateTimeOffset.Now
        );
        FireSnapshot(_lastSnapshot);
    }

    public void PersistProgressTick(
        PlayerState s,
        Action<IEnumerable<Episode>> refreshUi,
        IEnumerable<Episode> allEpisodes)
    {
        if (_current is null) return;

        long effLenMs, posMs;
        var endNow = IsEndReached(s, out effLenMs, out posMs);

        if (!_progressSeenForSession)
        {
            if (posMs > 0 || (s.IsPlaying && s.Length.HasValue && s.Length.Value > TimeSpan.Zero))
            {
                _progressSeenForSession = true;
                FireStatus(PlaybackStatus.Playing);
                CancelStallWatch();
            }
        }

        if (effLenMs > 0)
            _current.DurationMs = effLenMs;
        _current.Progress.LastPosMs = Math.Max(0, posMs);

        var snap = PlaybackSnapshot.From(
            _sid,
            _current.Id,
            TimeSpan.FromMilliseconds(Math.Max(0, posMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, effLenMs)),
            s.IsPlaying,
            s.Speed,
            DateTimeOffset.Now
        );
        _lastSnapshot = snap;
        FireSnapshot(snap);

        if (effLenMs > 0)
        {
            var ratio  = (double)posMs / effLenMs;
            var remain = TimeSpan.FromMilliseconds(Math.Max(0, effLenMs - posMs));

            var isVeryShort = effLenMs <= 60000;
            var remainCut   = isVeryShort ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
            var ratioCut    = isVeryShort ? 0.98 : 0.90;

            if (!_current.ManuallyMarkedPlayed && (ratio >= ratioCut || remain <= remainCut))
            {
                _current.ManuallyMarkedPlayed = true;
                _current.Progress.LastPlayedAt = DateTimeOffset.Now;
                _ = _saveAsync();
            }
        }

        if (!_endHandledForSession && endNow)
        {
            _endHandledForSession = true;
            FireStatus(PlaybackStatus.Ended);
            CancelStallWatch();

            if (_data.AutoAdvance &&
                (DateTimeOffset.Now - _lastAutoAdv) > TimeSpan.FromMilliseconds(500) &&
                TryFindNext(_current, out var nxt))
            {
                _lastAutoAdv = DateTimeOffset.Now;
                try { AutoAdvanceSuggested?.Invoke(nxt); } catch { }
            }
        }

        var now = DateTime.UtcNow;
        if ((now - _lastUiRefresh) > TimeSpan.FromSeconds(1))
        {
            _lastUiRefresh = now;
            try { refreshUi(allEpisodes); } catch { }
        }

        if ((now - _lastPeriodicSave) > TimeSpan.FromSeconds(3))
        {
            _lastPeriodicSave = now;
            _ = _saveAsync();
        }
    }

    #endregion

    #region helpers

    private void StartOneShotResume(long ms, int sid)
    {
        _resumeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resumeCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (sid != _sid) return;

                Application.MainLoop?.Invoke(() =>
                {
                    try
                    {
                        if (sid != _sid) return;
                        var lenMs = _audioPlayer.State.Length?.TotalMilliseconds ?? 0;
                        if (lenMs > 0 && ms > lenMs - 10000) return;
                        _audioPlayer.SeekTo(TimeSpan.FromMilliseconds(ms));
                    }
                    catch { }
                });
            }
            catch (TaskCanceledException) { }
            catch { }
        });
    }

    private void CancelResume()
    {
        try { _resumeCts?.Cancel(); } catch { }
        _resumeCts = null;
    }

    private void StartStallWatch(int sid, TimeSpan timeout)
    {
        CancelStallWatch();
        var cts = new CancellationTokenSource();
        _stallCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(timeout, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (sid != _sid) return;
                if (_progressSeenForSession) return;
                FireStatus(PlaybackStatus.SlowNetwork);
            }
            catch (TaskCanceledException) { }
            catch { }
        });
    }

    private void CancelStallWatch()
    {
        try { _stallCts?.Cancel(); } catch { }
        _stallCts = null;
    }

    private bool IsEndReached(PlayerState s, out long effLenMs, out long posMs)
    {
        var lenMsPlayer = (long)(s.Length?.TotalMilliseconds ?? 0);
        var lenMsMeta   = (long)(_current?.DurationMs!);
        posMs = (long)Math.Max(0, s.Position.TotalMilliseconds);

        effLenMs = Math.Max(Math.Max(lenMsPlayer, lenMsMeta), posMs);
        if (effLenMs <= 0) return false;

        if (lenMsPlayer > 0)
        {
            var remainPlayer = Math.Max(0, lenMsPlayer - posMs);
            var ratioPlayer  = (double)posMs / Math.Max(1, lenMsPlayer);

            if (ratioPlayer >= 0.995 || (!s.IsPlaying && remainPlayer <= 2000))
                return true;

            if (!s.IsPlaying && posMs >= Math.Max(0, lenMsPlayer - 250))
                return true;
        }

        var remainEff = Math.Max(0, effLenMs - posMs);
        var ratioEff  = (double)posMs / Math.Max(1, effLenMs);

        if (ratioEff >= 0.995 || (!s.IsPlaying && remainEff <= 500))
            return true;

        return false;
    }

    private bool TryFindNext(Episode current, out Episode next)
    {
        while (_data.Queue.Count > 0)
        {
            var nextId = _data.Queue[0];
            _data.Queue.RemoveAt(0);
            QueueChangedSafe();

            var cand = _data.Episodes.FirstOrDefault(e => e.Id == nextId);
            if (cand != null)
            {
                next = cand;
                _ = _saveAsync();
                return true;
            }
        }

        next = null!;
        var list = _data.Episodes
            .Where(e => e.FeedId == current.FeedId)
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();

        var idx = list.FindIndex(e => e.Id == current.Id);
        if (idx < 0) return false;

        for (int i = idx + 1; i < list.Count; i++)
        {
            var cand = list[i];
            if (_data.UnplayedOnly)
            {
                if (!cand.ManuallyMarkedPlayed) { next = cand; return true; }
            }
            else
            {
                next = cand; return true;
            }
        }

        if (_data.WrapAdvance)
        {
            for (int i = 0; i < idx; i++)
            {
                var cand = list[i];
                if (_data.UnplayedOnly)
                {
                    if (!cand.ManuallyMarkedPlayed) { next = cand; return true; }
                }
                else
                {
                    next = cand; return true;
                }
            }
        }

        return false;
    }

    private void ConsumeQueueUpToInclusive(Guid targetId)
    {
        if (_data.Queue.Count == 0) return;

        var ix = _data.Queue.IndexOf(targetId);
        if (ix < 0) return;

        _data.Queue.RemoveRange(0, ix + 1);
        QueueChangedSafe();
        _ = _saveAsync();
    }

    private void QueueChangedSafe()
    {
        try { QueueChanged?.Invoke(); } catch { }
    }

    private void FireStatus(PlaybackStatus status)
    {
        try { StatusChanged?.Invoke(status); } catch { }
    }

    #endregion

    #region snapshot api

    public PlaybackSnapshot GetLastSnapshot() => _lastSnapshot;

    private void FireSnapshot(PlaybackSnapshot snap)
    {
        try { SnapshotAvailable?.Invoke(snap); } catch { }
    }

    #endregion
}

#endregion

#region snapshot and status

public readonly record struct PlaybackSnapshot(
    int SessionId,
    Guid? EpisodeId,
    TimeSpan Position,
    TimeSpan Length,
    bool IsPlaying,
    double Speed,
    DateTimeOffset Timestamp)
{
    public static readonly PlaybackSnapshot Empty = new(
        SessionId: 0, EpisodeId: null,
        Position: TimeSpan.Zero, Length: TimeSpan.Zero,
        IsPlaying: false, Speed: 1.0,
        Timestamp: DateTimeOffset.MinValue
    );

    public static PlaybackSnapshot From(
        int sessionId,
        Guid? episodeId,
        TimeSpan position,
        TimeSpan length,
        bool isPlaying,
        double speed,
        DateTimeOffset now) => new(
            sessionId, episodeId,
            position < TimeSpan.Zero ? TimeSpan.Zero : position,
            length   < TimeSpan.Zero ? TimeSpan.Zero : length,
            isPlaying,
            speed <= 0 ? 1.0 : speed,
            now);
}

public enum PlaybackStatus
{
    Idle = 0,
    Loading = 1,
    SlowNetwork = 2,
    Playing = 3,
    Ended = 4
}

#endregion
