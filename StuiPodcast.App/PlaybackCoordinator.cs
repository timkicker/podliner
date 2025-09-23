using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.App.Debug;

sealed class PlaybackCoordinator
{
    // ---- Dependencies / State --------------------------------------------------
    readonly AppData _data;
    readonly IPlayer _player;
    readonly Func<Task> _saveAsync;
    readonly MemoryLogSink _mem;

    Episode? _current;
    CancellationTokenSource? _resumeCts;

    DateTime _lastUiRefresh     = DateTime.MinValue;
    DateTime _lastPeriodicSave  = DateTime.MinValue;
    DateTimeOffset _lastAutoAdvanceAt = DateTimeOffset.MinValue;

    // Genau-einmal-Guard pro abgespielter Episode
    bool _endHandledForSession = false;

    public event Action<Episode>? AutoAdvanceSuggested;

    // NEW: Queue-Änderungen signalisieren (damit UI „⧉ Queue“-Feed aktualisiert)
    public event Action? QueueChanged;

    public PlaybackCoordinator(AppData data, IPlayer player, Func<Task> saveAsync, MemoryLogSink mem)
    {
        _data = data;
        _player = player;
        _saveAsync = saveAsync;
        _mem = mem;
    }

    // ---- Public API ------------------------------------------------------------

    /// <summary>
    /// Startet das Abspielen einer Episode. Schneidet, falls nötig, die Queue
    /// bis inkl. dieser Episode (FIFO) ab – das deckt den Klick im Queue-Feed ab.
    /// Führt optional ein einmaliges Resume-Seek aus (mit kurzer Verzögerung),
    /// um LibVLC-Zickereien zu vermeiden.
    /// </summary>
    public void Play(Episode ep)
    {
        // Wenn die Episode in der Queue ist: alles bis inkl. dieser ID entfernen.
        ConsumeQueueUpToInclusive(ep.Id);

        _current = ep;
        _endHandledForSession = false; // Reset pro Session

        _current.LastPlayedAt = DateTimeOffset.Now;
        _ = _saveAsync(); // Save ist intern gedrosselt

        CancelResume();

        long? startMs = ep.LastPosMs;
        if (startMs is long ms)
        {
            long knownLen = ep.LengthMs ?? 0;
            // Resume überspringen, wenn sehr nahe an Anfang/Ende (bessere UX)
            if ((knownLen > 0 && (ms < 5_000 || ms > knownLen - 10_000)) ||
                (knownLen == 0 && ms < 5_000))
                startMs = null;
        }

        // Erst sauber starten, Resume optional in einem zweiten Tick
        _player.Play(ep.AudioUrl, null);

        if (startMs is long want && want > 0)
            StartOneShotResume(want);
    }

    /// <summary>
    /// Wird regelmäßig aus Player.StateChanged + UI-Timer aufgerufen.
    /// Persistiert Fortschritt, markiert Played, schlägt Auto-Advance vor und
    /// triggert leichte UI-Refreshs.
    /// </summary>
    public void PersistProgressTick(
        PlayerState s,
        Action<IEnumerable<Episode>> refreshUi,
        IEnumerable<Episode> allEpisodes)
    {
        if (_current is null) return;

        // Eff-Länge & Pos bestimmen + Ende robust erkennen
        long effLenMs, posMs;
        var endNow = IsEndReached(s, out effLenMs, out posMs);

        // Persistiere bekannte Länge & Position
        if (effLenMs > 0) _current.LengthMs = effLenMs;
        _current.LastPosMs = posMs;

        // Played-Markierung (unabhängig vom Auto-Advance)
        if (effLenMs > 0)
        {
            var ratio  = (double)posMs / effLenMs;
            var remain = TimeSpan.FromMilliseconds(Math.Max(0, effLenMs - posMs));

            if (!_current.Played && (ratio >= 0.90 || remain <= TimeSpan.FromSeconds(30)))
            {
                _current.Played = true;
                _current.LastPlayedAt = DateTimeOffset.Now;
                _ = _saveAsync(); // schnelles Save bei Statuswechsel
            }
        }

        // Auto-Advance: genau einmal pro Session, entkoppelt von Played-Markierung
        if (!_endHandledForSession && endNow)
        {
            _endHandledForSession = true;

            if (_data.AutoAdvance &&
                (DateTimeOffset.Now - _lastAutoAdvanceAt) > TimeSpan.FromMilliseconds(500) &&
                TryFindNext(_current, out var nxt))
            {
                _lastAutoAdvanceAt = DateTimeOffset.Now;
                try { AutoAdvanceSuggested?.Invoke(nxt); } catch { /* best effort */ }
            }
        }

        // UI leicht aktualisieren (~1x/s)
        var now = DateTime.UtcNow;
        if ((now - _lastUiRefresh) > TimeSpan.FromSeconds(1))
        {
            _lastUiRefresh = now;
            try { refreshUi(allEpisodes); } catch { /* robust */ }
        }

        // periodisches Save (~3s)
        if ((now - _lastPeriodicSave) > TimeSpan.FromSeconds(3))
        {
            _lastPeriodicSave = now;
            _ = _saveAsync();
        }
    }

    // ---- Internals -------------------------------------------------------------

    // Einmaliger Resume-Seek, etwas verzögert (stabiler bei LibVLC)
    void StartOneShotResume(long ms)
    {
        _resumeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resumeCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, cts.Token); // Decoder/Length settle lassen
                if (cts.IsCancellationRequested) return;

                Application.MainLoop?.Invoke(() =>
                {
                    try
                    {
                        var lenMs = _player.State.Length?.TotalMilliseconds ?? 0;
                        if (lenMs > 0 && ms > lenMs - 10_000) return; // nahe Ende ignorieren
                        _player.SeekTo(TimeSpan.FromMilliseconds(ms));
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

    /// <summary>
    /// Robuste End-Erkennung.
    /// - Wenn Player-Länge bekannt: End-Kriterien gegen *Player*-Länge prüfen.
    /// - Fallback: gegen effektive Länge (max(PlayerLen, MetaLen, Pos)).
    /// - Außerdem: „End-Stall“: IsPlaying==false und Position ~ Länge.
    /// </summary>
    bool IsEndReached(PlayerState s, out long effLenMs, out long posMs)
    {
        var lenMsPlayer = (long)(s.Length?.TotalMilliseconds ?? 0);
        var lenMsMeta   = (long)(_current?.LengthMs ?? 0);
        posMs = (long)Math.Max(0, s.Position.TotalMilliseconds);

        effLenMs = Math.Max(Math.Max(lenMsPlayer, lenMsMeta), posMs); // nie kleiner als Pos
        if (effLenMs <= 0) return false;

        // Primär gegen die *Player*-Länge prüfen (am zuverlässigsten)
        if (lenMsPlayer > 0)
        {
            var remainPlayer = Math.Max(0, lenMsPlayer - posMs);
            var ratioPlayer  = (double)posMs / Math.Max(1, lenMsPlayer);

            // 99.5% oder weniger als 2s Rest → Ende
            if (ratioPlayer >= 0.995 || (!s.IsPlaying && remainPlayer <= 2000))
                return true;

            // „End-Stall“: nicht spielend & pos >= (lenPlayer - 250ms)
            if (!s.IsPlaying && posMs >= Math.Max(0, lenMsPlayer - 250))
                return true;
        }

        // Fallback gegen effLenMs (falls Player-Länge 0 bleibt)
        var remainEff = Math.Max(0, effLenMs - posMs);
        var ratioEff  = (double)posMs / Math.Max(1, effLenMs);

        if (ratioEff >= 0.995 || (!s.IsPlaying && remainEff <= 500))
            return true;

        return false;
    }

    /// <summary>
    /// Nächste Episode bestimmen:
    ///   1) Erst aus der Queue (FIFO, ungültige Ids werden übersprungen)
    ///   2) Sonst in Feed-Reihenfolge (pubdate desc), optional Wrap & UnplayedOnly
    /// </summary>
    bool TryFindNext(Episode current, out Episode next)
    {
        // (1) Queue zuerst
        while (_data.Queue.Count > 0)
        {
            var nextId = _data.Queue[0];
            _data.Queue.RemoveAt(0);             // FIFO
            QueueChangedSafe();
            var cand = _data.Episodes.FirstOrDefault(e => e.Id == nextId);
            if (cand != null)
            {
                next = cand;
                _ = _saveAsync(); // Queue-Verbrauch persistieren
                return true;
            }
        }

        // (2) Fallback: normale Feed-Reihenfolge
        next = null!;
        var list = _data.Episodes
            .Where(e => e.FeedId == current.FeedId)
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();

        var idx = list.FindIndex(e => e.Id == current.Id);
        if (idx < 0) return false;

        // nach unten (ältere) suchen
        for (int i = idx + 1; i < list.Count; i++)
        {
            var cand = list[i];
            if (_data.UnplayedOnly)
            {
                if (!cand.Played) { next = cand; return true; }
            }
            else
            {
                next = cand; return true;
            }
        }

        // optional: wrap-around
        if (_data.WrapAdvance)
        {
            for (int i = 0; i < idx; i++)
            {
                var cand = list[i];
                if (_data.UnplayedOnly)
                {
                    if (!cand.Played) { next = cand; return true; }
                }
                else
                {
                    next = cand; return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Schneidet die Queue bis inkl. targetId; feuert QueueChanged.
    /// </summary>
    void ConsumeQueueUpToInclusive(Guid targetId)
    {
        if (_data.Queue.Count == 0) return;

        var ix = _data.Queue.IndexOf(targetId);
        if (ix < 0) return;

        // Alles bis inkl. ix entfernen
        _data.Queue.RemoveRange(0, ix + 1);
        QueueChangedSafe();
        _ = _saveAsync();
    }

    void QueueChangedSafe()
    {
        try { QueueChanged?.Invoke(); } catch { /* best effort */ }
    }
}
