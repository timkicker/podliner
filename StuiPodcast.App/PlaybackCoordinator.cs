using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;
using StuiPodcast.Infra.Player;

/// <summary>
/// Koordiniert Playback-Fortschritt, Persistenz, Auto-Advance und liefert
/// pro Tick einen *atomischen* Fortschritts-Snapshot (Position & Länge).
/// Öffentliche API bleibt kompatibel: Play(), PersistProgressTick(), bestehende Events.
/// Ergänzt um: Stall-Watchdog + Status-Events (Loading/Slow/Playing/Ended).
/// </summary>
sealed class PlaybackCoordinator
{
    // ---- Dependencies / State --------------------------------------------------
    private readonly AppData _data;
    private readonly IPlayer _player;
    private readonly Func<Task> _saveAsync;
    private readonly MemoryLogSink _mem;

    private Episode? _current;

    // Resume-Timer
    private CancellationTokenSource? _resumeCts;

    // Stall-Watchdog (für „Connecting… (slow)“)
    private CancellationTokenSource? _stallCts;
    private bool _progressSeenForSession = false;

    // Watchdogs / Throttles
    private DateTime _lastUiRefresh    = DateTime.MinValue;
    private DateTime _lastPeriodicSave = DateTime.MinValue;
    private DateTimeOffset _lastAutoAdv = DateTimeOffset.MinValue;

    // Genau-einmal-Guard pro abgespielter Episode
    private bool _endHandledForSession = false;

    // Session-Isolation: Jede Play-Session bekommt eine ID.
    private int _sid = 0;

    public event Action<Episode>? AutoAdvanceSuggested;

    // Optional: UI kann darauf reagieren (Queue-Feed neu rendern).
    public event Action? QueueChanged;

    /// <summary>
    /// Neuer, atomischer Fortschritts-Snapshot pro Tick (Position & Länge).
    /// Die UI wird diesen in PlayerPanel/Shell konsumieren.
    /// </summary>
    public event Action<PlaybackSnapshot>? SnapshotAvailable;

    /// <summary>
    /// Status-Event für UI (Loading → SlowNetwork → Playing/Ended).
    /// Nicht zwingend zu abonnieren; bricht bestehende API nicht.
    /// </summary>
    public event Action<PlaybackStatus>? StatusChanged;

    private PlaybackSnapshot _lastSnapshot = PlaybackSnapshot.Empty;

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
    /// um LibVLC-Zickereien zu vermeiden. Setzt „Loading“-Status und startet
    /// einen Stall-Watchdog, der bei ausbleibendem Fortschritt „SlowNetwork“ meldet.
    /// </summary>
    public void Play(Episode ep)
    {
        // neue Session
        unchecked { _sid++; }
        var sid = _sid;

        // Wenn die Episode in der Queue ist: alles bis inkl. dieser ID entfernen.
        ConsumeQueueUpToInclusive(ep.Id);

        _current = ep;
        _endHandledForSession = false;
        _progressSeenForSession = false;

        _current.Progress.LastPlayedAt = DateTimeOffset.Now;
        _ = _saveAsync(); // Save ist intern gedrosselt

        CancelResume();
        CancelStallWatch();

        // Startoffset heuristisch:
        // - sehr nahe am Anfang → nicht resumen
        // - sehr nahe am Ende → nicht resumen (sonst „hängen“ wir am Ende)
        long? startMs = ep.Progress.LastPosMs;
        if (startMs is long ms)
        {
            long knownLen = ep.DurationMs;
            if ((knownLen > 0 && (ms < 5_000 || ms > knownLen - 10_000)) ||
                (knownLen == 0 && ms < 5_000))
            {
                startMs = null;
            }
        }

        // 1) Erst sauber starten (ohne Startoffset, stabilisiert Demux)
        FireStatus(PlaybackStatus.Loading);
        _player.Play(ep.AudioUrl, null);

        // 2) Danach einmalig resumen, wenn gewünscht – aber session-sicher
        if (startMs is long want && want > 0)
            StartOneShotResume(want, sid);

        // 3) Stall-Watchdog starten (z. B. 5 s bis „SlowNetwork“)
        StartStallWatch(sid, TimeSpan.FromSeconds(5));

        // 4) Snapshot auf definierte Ausgangswerte setzen
        _lastSnapshot = PlaybackSnapshot.From(
            sid,
            ep.Id,
            TimeSpan.Zero,
            _player.State.Length ?? TimeSpan.Zero,
            _player.State.IsPlaying,
            _player.State.Speed,
            DateTimeOffset.Now
        );
        FireSnapshot(_lastSnapshot);
    }

    /// <summary>
    /// Wird regelmäßig aus Player.StateChanged + UI-Timer aufgerufen.
    /// Persistiert Fortschritt, markiert Played, schlägt Auto-Advance vor und
    /// triggert leichte UI-Refreshs. Außerdem erzeugt sie genau EINEN
    /// atomischen Fortschritts-Snapshot (Position & Länge) für die UI.
    /// </summary>
    public void PersistProgressTick(
        PlayerState s,
        Action<IEnumerable<Episode>> refreshUi,
        IEnumerable<Episode> allEpisodes)
    {
        if (_current is null) return;

        // 1) Effektive Länge & Position berechnen (einmal pro Tick!)
        long effLenMs, posMs;
        var endNow = IsEndReached(s, out effLenMs, out posMs);

        // Fortschritt für Watchdog bewerten
        if (!_progressSeenForSession)
        {
            // „Fortschritt gesehen“, sobald Position > 0 ODER wir Playing+Length haben
            if (posMs > 0 || (s.IsPlaying && s.Length.HasValue && s.Length.Value > TimeSpan.Zero))
            {
                _progressSeenForSession = true;
                FireStatus(PlaybackStatus.Playing);
                CancelStallWatch();
            }
        }

        // 2) Persistiere bekannte Länge & Position (clamps)
        if (effLenMs > 0)
            _current.DurationMs = effLenMs;
        _current.Progress.LastPosMs = Math.Max(0, posMs);

        // 3) Snapshot bilden (einmalig) und ausgeben
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

        // 4) Played-Markierung (unabhängig vom Auto-Advance)
        if (effLenMs > 0)
        {
            var ratio  = (double)posMs / effLenMs;
            var remain = TimeSpan.FromMilliseconds(Math.Max(0, effLenMs - posMs));

            // Für sehr kurze Clips härten (keine zu frühe Markierung)
            var isVeryShort = effLenMs <= 60_000; // <= 60s
            var remainCut   = isVeryShort ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
            var ratioCut    = isVeryShort ? 0.98 : 0.90;

            if (!_current.ManuallyMarkedPlayed && (ratio >= ratioCut || remain <= remainCut))
            {
                _current.ManuallyMarkedPlayed = true;
                _current.Progress.LastPlayedAt = DateTimeOffset.Now;
                _ = _saveAsync(); // schnelles Save bei Statuswechsel
            }
        }

        // 5) Auto-Advance: genau einmal pro Session, entkoppelt von Played-Markierung
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
                try { AutoAdvanceSuggested?.Invoke(nxt); } catch { /* best effort */ }
            }
        }

        // 6) UI leicht aktualisieren (~1x/s)
        var now = DateTime.UtcNow;
        if ((now - _lastUiRefresh) > TimeSpan.FromSeconds(1))
        {
            _lastUiRefresh = now;
            try { refreshUi(allEpisodes); } catch { /* robust */ }
        }

        // 7) periodisches Save (~3s)
        if ((now - _lastPeriodicSave) > TimeSpan.FromSeconds(3))
        {
            _lastPeriodicSave = now;
            _ = _saveAsync();
        }
    }

    // ---- Internals -------------------------------------------------------------

    // Einmaliger Resume-Seek, leicht verzögert (stabiler bei LibVLC); session-sicher.
    private void StartOneShotResume(long ms, int sid)
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

                // Session noch dieselbe?
                if (sid != _sid) return;

                Application.MainLoop?.Invoke(() =>
                {
                    try
                    {
                        if (sid != _sid) return; // Session-Check auch hier
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

    private void CancelResume()
    {
        try { _resumeCts?.Cancel(); } catch { }
        _resumeCts = null;
    }

    /// <summary>
    /// Startet den Stall-Watchdog: Wenn innerhalb von „timeout“ kein Fortschritt
    /// (Position>0 oder spielende Engine mit bekannter Länge) gesehen wird, wird
    /// ein „SlowNetwork“-Status gemeldet (Session-sicher).
    /// </summary>
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
                if (sid != _sid) return;               // Session gewechselt
                if (_progressSeenForSession) return;   // Bereits Fortschritt
                FireStatus(PlaybackStatus.SlowNetwork);
                // (Log-Zeile entfernt – MemoryLogSink hat keine Log-Methode)
            }
            catch (TaskCanceledException) { }
            catch { /* ignore */ }
        });
    }

    private void CancelStallWatch()
    {
        try { _stallCts?.Cancel(); } catch { }
        _stallCts = null;
    }

    /// <summary>
    /// Robuste End-Erkennung.
    /// - Priorität: *Player*-Länge (falls vorhanden).
    /// - Fallback: effektive Länge = max(PlayerLen, MetaLen, Pos).
    /// - Stall-Fälle: IsPlaying==false & (Rest sehr klein bzw. pos≈len).
    /// </summary>
    private bool IsEndReached(PlayerState s, out long effLenMs, out long posMs)
    {
        var lenMsPlayer = (long)(s.Length?.TotalMilliseconds ?? 0);
        var lenMsMeta   = (long)(_current?.DurationMs);
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
    private bool TryFindNext(Episode current, out Episode next)
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
                if (!cand.ManuallyMarkedPlayed) { next = cand; return true; }
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

    /// <summary>
    /// Schneidet die Queue bis inkl. targetId; feuert QueueChanged.
    /// </summary>
    private void ConsumeQueueUpToInclusive(Guid targetId)
    {
        if (_data.Queue.Count == 0) return;

        var ix = _data.Queue.IndexOf(targetId);
        if (ix < 0) return;

        // Alles bis inkl. ix entfernen
        _data.Queue.RemoveRange(0, ix + 1);
        QueueChangedSafe();
        _ = _saveAsync();
    }

    private void QueueChangedSafe()
    {
        try { QueueChanged?.Invoke(); } catch { /* best effort */ }
    }

    private void FireStatus(PlaybackStatus status)
    {
        try { StatusChanged?.Invoke(status); } catch { /* UI darf App nicht crashen */ }
    }

    // ---- Snapshot-API ----------------------------------------------------------

    /// <summary>
    /// Liefert den letzten erzeugten Snapshot (z. B. UI-Initialisierung).
    /// </summary>
    public PlaybackSnapshot GetLastSnapshot() => _lastSnapshot;

    private void FireSnapshot(PlaybackSnapshot snap)
    {
        try { SnapshotAvailable?.Invoke(snap); } catch { /* UI darf App nicht crashen */ }
    }
}

/// <summary>
/// Atomischer Fortschritts-Snapshot: beide Anzeigen (Elapsed & Remaining)
/// basieren auf denselben Werten innerhalb eines UI-Frames.
/// </summary>
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

/// <summary>
/// Loading → SlowNetwork (falls kein Fortschritt) → Playing/Ended.
/// </summary>
public enum PlaybackStatus
{
    Idle = 0,
    Loading = 1,
    SlowNetwork = 2,
    Playing = 3,
    Ended = 4
}
