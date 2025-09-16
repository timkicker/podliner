using Serilog;
using StuiPodcast.Core;
using VLC = LibVLCSharp.Shared;

namespace StuiPodcast.Infra;

public interface IPlayer {
    event Action<PlayerState>? StateChanged;
    PlayerState State { get; }
    void Play(string url);
    void TogglePause();
    void SeekRelative(TimeSpan delta);
    void SeekTo(TimeSpan position);
    void SetVolume(int vol0to100);
    void SetSpeed(double speed);
    void Stop();
}

public sealed class LibVlcPlayer : IPlayer, IDisposable {
    private readonly VLC.LibVLC _lib;
    private readonly VLC.MediaPlayer _mp;     // EIN Player für die ganze Laufzeit
    private VLC.Media? _media;                 // aktuelle Media-Referenz halten
    private readonly object _sync = new();     // serialisiert alle Zugriffe
    private TaskCompletionSource<bool>? _stoppedTcs;
    private int _sessionId;

    public PlayerState State { get; } = new();
    public event Action<PlayerState>? StateChanged;

    public LibVlcPlayer() {
        VLC.Core.Initialize();

        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "stui-vlc.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var opts = new[]{
            "--no-video-title-show",
            "--quiet", "--verbose=0", "--no-color",
            "--no-xlib",
            "--file-logging", $"--logfile={logPath}",
            "--network-caching=2000"   // etwas Puffer gegen Netzwerk-Ruckler
        };
        _lib = new VLC.LibVLC(opts);

        _mp = new VLC.MediaPlayer(_lib);

        // Events nur EINMAL anbinden (wir entsorgen den Player nicht ständig)
        _mp.TimeChanged        += OnTimeChanged;
        _mp.LengthChanged      += OnLengthChanged;
        _mp.EndReached         += (_,__) => { lock(_sync){ State.IsPlaying = false; StateChanged?.Invoke(State); } };
        _mp.EncounteredError   += (_,__) => { lock(_sync){ State.IsPlaying = false; StateChanged?.Invoke(State); } };
        _mp.Stopped            += (_,__) => { lock(_sync){ _stoppedTcs?.TrySetResult(true); _stoppedTcs = null; } };

        // Startwerte
        _mp.Volume = State.Volume0_100;
        _mp.SetRate((float)State.Speed);

        Log.Debug("LibVLC init + MediaPlayer ready");
    }

    public void Play(string url) {
        lock (_sync) {
            _sessionId++;
            var sid = _sessionId;
            Log.Information("[#{sid}] Play {url}", sid, url);

            // vorherige Wiedergabe sauber stoppen
            StopInternalLocked(sid);

            // altes Media weg
            SafeDisposeMediaLocked(sid);

            // neues Media setzen und starten
            _media = new VLC.Media(_lib, new Uri(url));
            var started = _mp.Play(_media);     // overload mit Media → _media bleibt gültig
            State.IsPlaying = started;
            StateChanged?.Invoke(State);

            Log.Debug("[#{sid}] Play started={started}", sid, started);
        }
    }

    public void TogglePause() {
        lock (_sync) {
            if (State.IsPlaying) _mp.Pause();
            else _mp.Play();
            State.IsPlaying = !State.IsPlaying;
            StateChanged?.Invoke(State);
        }
    }

    public void SeekRelative(TimeSpan delta) {
        lock (_sync) {
            var t = Math.Max(0, _mp.Time + (long)delta.TotalMilliseconds);
            _mp.Time = t;
        }
    }

    public void SeekTo(TimeSpan position) {
        lock (_sync) {
            _mp.Time = Math.Max(0, (long)position.TotalMilliseconds);
        }
    }

    public void SetVolume(int vol0to100) {
        lock (_sync) {
            vol0to100 = Math.Clamp(vol0to100, 0, 100);
            State.Volume0_100 = vol0to100;
            _mp.Volume = vol0to100;
            StateChanged?.Invoke(State);
        }
    }

    public void SetSpeed(double speed) {
        lock (_sync) {
            speed = Math.Clamp(speed, 0.5, 2.5);
            State.Speed = speed;
            _mp.SetRate((float)speed);
            StateChanged?.Invoke(State);
        }
    }

    public void Stop() {
        lock (_sync) StopInternalLocked(_sessionId);
    }

    private void StopInternalLocked(int sid) {
        try {
            // nur wenn gerade etwas läuft
            if (_mp.IsPlaying) {
                Log.Debug("[#{sid}] Stop()", sid);
                _stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try { _mp.Stop(); } catch (Exception ex) { Log.Debug(ex, "[#{sid}] _mp.Stop threw", sid); }

                // bis zu 1.5s auf Stopped warten (Event kommt aus native Thread)
                try { _stoppedTcs.Task.Wait(TimeSpan.FromMilliseconds(1500)); } catch { /* timeout egal */ }
                _stoppedTcs = null;
            }
        } finally {
            State.IsPlaying = false;
            StateChanged?.Invoke(State);
        }
    }

    private void SafeDisposeMediaLocked(int sid) {
        try {
            var m = _media;
            _media = null;
            if (m != null) {
                // Dispose im Hintergrund, kurz warten (vermeidet Arbeit auf native Thread)
                var t = Task.Run(() => { try { m.Dispose(); } catch (Exception ex) { Log.Debug(ex, "[#{sid}] media.Dispose", sid); } });
                t.Wait(TimeSpan.FromMilliseconds(200));
            }
        } catch (Exception ex) {
            Log.Debug(ex, "[#{sid}] media.Dispose wait", sid);
        }
    }

    private void OnTimeChanged(object? _, VLC.MediaPlayerTimeChangedEventArgs e) {
        // klein & robust halten – keine Disposes von hier
        try {
            State.Position = TimeSpan.FromMilliseconds(e.Time);
            var len = _mp.Length;
            if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);
            StateChanged?.Invoke(State);
        } catch (Exception ex) {
            Log.Debug(ex, "OnTimeChanged");
        }
    }

    private void OnLengthChanged(object? _, VLC.MediaPlayerLengthChangedEventArgs e) {
        try {
            State.Length = e.Length > 0 ? TimeSpan.FromMilliseconds(e.Length) : null;
            StateChanged?.Invoke(State);
        } catch (Exception ex) {
            Log.Debug(ex, "OnLengthChanged");
        }
    }

    public void Dispose() {
        lock (_sync) {
            Log.Information("LibVlcPlayer.Dispose");
            try { StopInternalLocked(_sessionId); } catch (Exception ex) { Log.Debug(ex, "StopInternalLocked in Dispose"); }
            SafeDisposeMediaLocked(_sessionId);

            // Events ab – _mp lebt nur hier, danach entsorgen
            try {
                _mp.TimeChanged      -= OnTimeChanged;
                _mp.LengthChanged    -= OnLengthChanged;
            } catch { }

            try { _mp.Dispose(); } catch (Exception ex) { Log.Debug(ex, "_mp.Dispose"); }
            try { _lib.Dispose(); } catch (Exception ex) { Log.Debug(ex, "_lib.Dispose"); }
        }
    }
}
