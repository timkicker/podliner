using StuiPodcast.App.Debug;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using Terminal.Gui;

namespace StuiPodcast.App;

#region playback coordinator

// coordinates playback progress, persistence and auto advance
public sealed class PlaybackCoordinator : IDisposable
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
    
    private CancellationTokenSource? _loadingCts;
    private TimeSpan _loadingBaseline = TimeSpan.Zero;
    private DateTime _loadingSinceUtc = DateTime.MinValue;

    private static readonly TimeSpan LoadingMinVisible    = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LoadingAdvanceThresh = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SlowLoadingAfter     = TimeSpan.FromSeconds(2);   // „slow“ ab 2 s
    private static readonly TimeSpan VerySlowLoadingAfter = TimeSpan.FromSeconds(6);   // optional: „very slow“


    private bool _endHandledForSession = false;
    private int _sid = 0;

    public event Action<Episode>? AutoAdvanceSuggested;
    public event Action? QueueChanged;
    public event Action<PlaybackSnapshot>? SnapshotAvailable;
    public event Action<PlaybackStatus>? StatusChanged;

    private PlaybackSnapshot _lastSnapshot = PlaybackSnapshot.Empty;

    // Services are the single source of truth for episodes and the playback
    // queue. The coordinator no longer falls back to AppData collections.
    private readonly StuiPodcast.App.Services.IEpisodeStore _episodes;
    private readonly StuiPodcast.App.Services.IQueueService _queue;

    public PlaybackCoordinator(AppData data, IAudioPlayer audioPlayer, Func<Task> saveAsync, MemoryLogSink mem,
                               StuiPodcast.App.Services.IEpisodeStore episodes,
                               StuiPodcast.App.Services.IQueueService queue)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        _mem = mem;
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
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
        
        CancelAndDispose(ref _loadingCts);
        _loadingCts = new CancellationTokenSource();

        _loadingBaseline = TimeSpan.FromMilliseconds(ep.Progress?.LastPosMs ?? 0);
        _loadingSinceUtc = DateTime.UtcNow;

        long? startMs = ep.Progress?.LastPosMs;
        if (startMs is { } ms)
        {
            long knownLen = ep.DurationMs;
            if ((knownLen > 0 && (ms < 5000 || ms > knownLen - 10000)) ||
                (knownLen == 0 && ms < 5000))
            {
                startMs = null;
            }
        }

        FireStatus(PlaybackStatus.Loading);
        
        // loading ui 
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SlowLoadingAfter, _loadingCts.Token);
                if (_loadingCts.IsCancellationRequested) return;
                if (sid != _sid) return;
                if (_progressSeenForSession) return; 

                FireStatus(PlaybackStatus.SlowNetwork);

                await Task.Delay(VerySlowLoadingAfter - SlowLoadingAfter, _loadingCts.Token);
                if (_loadingCts.IsCancellationRequested) return;
                if (sid != _sid) return;
                if (_progressSeenForSession) return;


                FireStatus(PlaybackStatus.SlowNetwork);
            }
            catch {  }
        });
        
        _ = Task.Run(() =>
        {
            try
            {
                _audioPlayer.Play(ep.AudioUrl, null);
            }
            catch
            {
            }
        });

        if (startMs is long want && want > 0)
            StartOneShotResume(want, sid);

        StartStallWatch(sid, TimeSpan.FromSeconds(5));

        _lastSnapshot = PlaybackSnapshot.From(
            sid,
            ep.Id,
            TimeSpan.Zero,
            TimeSpan.Zero,
            isPlaying: false,
            speed: 1.0,
            now: DateTimeOffset.Now
        );
        FireSnapshot(_lastSnapshot);

    }

    public void PersistProgressTick(
        PlayerState s,
        Action<IEnumerable<Episode>> refreshUi)
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
                try { _loadingCts?.Cancel(); } catch { } 
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
            try { _loadingCts?.Cancel(); } catch { } 
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

        // Progress display on the active row is now piggybacked on SnapshotAvailable
        // (see UiComposer.RefreshActiveProgress). The periodic full list refresh
        // that used to live here rebuilt every row every second — expensive and
        // redundant. The refreshUi callback is kept for structural fallbacks
        // but only fires every 5s to catch rare missed updates.
        if ((now - _lastUiRefresh) > TimeSpan.FromSeconds(5))
        {
            _lastUiRefresh = now;
            try { refreshUi(_episodes.Snapshot()); } catch { }
        }

        // Periodic progress persistence. Saves rewrite the entire library JSON
        // (feeds + episodes + queue + history), so doing this every 3s during
        // playback means writing multi-MB files ~1200 times per hour. 30s is
        // a better tradeoff: max 30s of progress lost on hard crash, orders
        // of magnitude less disk churn. Critical state changes (episode
        // marked played, queue mutations, etc.) still save immediately via
        // their own paths.
        if ((now - _lastPeriodicSave) > TimeSpan.FromSeconds(30))
        {
            _lastPeriodicSave = now;
            _ = _saveAsync();
        }
    }

    #endregion

    #region helpers

    private void StartOneShotResume(long ms, int sid)
    {
        CancelAndDispose(ref _resumeCts);
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

    private void CancelResume() => CancelAndDispose(ref _resumeCts);

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

    private void CancelStallWatch() => CancelAndDispose(ref _stallCts);

    // Cancel a CTS, dispose it, and null the field in one step. Old code only
    // cancelled and reassigned, leaking the CTS registration table on every Play.
    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        var c = cts;
        cts = null;
        if (c == null) return;
        try { c.Cancel(); } catch { }
        try { c.Dispose(); } catch { }
    }

    public void Dispose()
    {
        CancelAndDispose(ref _loadingCts);
        CancelAndDispose(ref _resumeCts);
        CancelAndDispose(ref _stallCts);
    }

    private bool IsEndReached(PlayerState s, out long effLenMs, out long posMs)
    {
        var lenMsPlayer = (long)(s.Length?.TotalMilliseconds ?? 0);
        var lenMsMeta   = (long)(_current?.DurationMs ?? 0);
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
        // Consume the queue front first.
        while (_queue.Count > 0)
        {
            var nextId = _queue.Snapshot()[0];
            _queue.Remove(nextId);
            QueueChangedSafe();

            var cand = _episodes.Find(nextId);
            if (cand != null)
            {
                next = cand;
                _ = _saveAsync();
                return true;
            }
        }

        next = null!;
        var list = _episodes.WhereByFeed(current.FeedId)
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
        if (_queue.Count == 0) return;
        if (!_queue.TrimUpToInclusive(targetId)) return;

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

    // Advance to the next episode (via queue or same-feed fallback)
    public bool TryAdvanceToNext(out Episode? next)
    {
        if (_current == null) { next = null; return false; }
        var found = TryFindNext(_current, out var ep);
        next = ep;
        return found;
    }

    // Return the episode published immediately before the current one in the feed
    public bool TryFindPrev(out Episode? prev)
    {
        if (_current == null) { prev = null; return false; }

        var list = _episodes.WhereByFeed(_current.FeedId)
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();

        var idx = list.FindIndex(e => e.Id == _current.Id);
        if (idx <= 0) { prev = null; return false; }

        prev = list[idx - 1];
        return true;
    }

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