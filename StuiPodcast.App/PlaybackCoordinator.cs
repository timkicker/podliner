using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.App.Debug;

sealed class PlaybackCoordinator
{
    readonly AppData _data;
    readonly IPlayer _player;
    readonly Func<Task> _saveAsync;
    readonly MemoryLogSink _mem;

    Episode? _current;
    CancellationTokenSource? _resumeCts;
    DateTime _lastUiRefresh = DateTime.MinValue;
    DateTime _lastPeriodicSave = DateTime.MinValue;

    public PlaybackCoordinator(AppData data, IPlayer player, Func<Task> saveAsync, MemoryLogSink mem)
    {
        _data = data;
        _player = player;
        _saveAsync = saveAsync;
        _mem = mem;
    }

    // start playback; do a single delayed seek for resume to avoid libvlc lockups
    public void Play(Episode ep)
    {
        _current = ep;
        CancelResume();

        long? startMs = ep.LastPosMs;
        if (startMs is long ms)
        {
            long knownLen = ep.LengthMs ?? 0;
            // skip resume if too close to start/end
            if ((knownLen > 0 && (ms < 5_000 || ms > knownLen - 10_000)) ||
                (knownLen == 0 && ms < 5_000))
                startMs = null;
        }

        // start clean, then resume once after a short delay (safer with libvlc)
        _player.Play(ep.AudioUrl, null);

        if (startMs is long want && want > 0)
            StartOneShotResume(want);
    }

    void StartOneShotResume(long ms)
    {
        _resumeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resumeCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                // let the decoder attach & length populate
                await Task.Delay(350, cts.Token);
                if (cts.IsCancellationRequested) return;

                Application.MainLoop?.Invoke(() =>
                {
                    try
                    {
                        var lenMs = _player.State.Length?.TotalMilliseconds ?? 0;
                        if (lenMs > 0 && ms > lenMs - 10_000) return; // ignore if too close to end
                        _player.SeekTo(TimeSpan.FromMilliseconds(ms));  // one single seek
                    }
                    catch { /* ignore */ }
                });
            }
            catch (TaskCanceledException) { }
            catch { /* ignore */ }
        });
    }

    void CancelResume()
    {
        try { _resumeCts?.Cancel(); } catch { }
        _resumeCts = null;
    }

    // called regularly (from StateChanged + UI timer) to persist progress & do light UI refresh
    public void PersistProgressTick(
        PlayerState s,
        Action<IEnumerable<Episode>> refreshUi,
        IEnumerable<Episode> allEpisodes)
    {
        if (_current is null) return;

        var lenMs = (long)(s.Length?.TotalMilliseconds ?? 0);
        var posMs = (long)Math.Max(0, s.Position.TotalMilliseconds);

        if (lenMs > 0) _current.LengthMs = lenMs;
        _current.LastPosMs = posMs;

        if (lenMs > 0)
        {
            var remain = TimeSpan.FromMilliseconds(Math.Max(0, lenMs - posMs));
            var ratio = (double)posMs / lenMs;
            if (ratio >= 0.90 || remain <= TimeSpan.FromSeconds(30))
            {
                if (!_current.Played)
                {
                    _current.Played = true;
                    _current.LastPlayedAt = DateTimeOffset.Now;
                    _ = _saveAsync(); // quick save when marking played
                }
            }
        }

        var now = DateTime.UtcNow;

        // refresh episodes about 1x/sec
        if ((now - _lastUiRefresh) > TimeSpan.FromSeconds(1))
        {
            _lastUiRefresh = now;
            refreshUi(allEpisodes);
        }

        // periodic save ~3s
        if ((now - _lastPeriodicSave) > TimeSpan.FromSeconds(3))
        {
            _lastPeriodicSave = now;
            _ = _saveAsync();
        }
    }
}
