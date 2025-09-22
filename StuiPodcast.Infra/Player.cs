using Serilog;
using StuiPodcast.Core;
using VLC = LibVLCSharp.Shared;

namespace StuiPodcast.Infra;


public interface IPlayer {
    event Action<PlayerState>? StateChanged;
    PlayerState State { get; }
    void Play(string url, long? startMs = null);
    void TogglePause();
    void SeekRelative(TimeSpan delta);
    void SeekTo(TimeSpan position);
    void SetVolume(int vol0to100);
    void SetSpeed(double speed);
    void Stop();
}

public sealed class LibVlcPlayer : IPlayer, IDisposable {
    private readonly VLC.LibVLC _lib;
    private readonly VLC.MediaPlayer _mp; // EIN MediaPlayer über die ganze App
    private VLC.Media? _media;
    private readonly object _sync = new();
    private TaskCompletionSource<bool>? _stoppedTcs;
    private int _sessionId;
    private long? _pendingSeekMs;
    
    
    
    public PlayerState State { get; } = new();
    public event Action<PlayerState>? StateChanged;

    public LibVlcPlayer() {
        VLC.Core.Initialize();

        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "stui-vlc.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var opts = new[]{
            "--no-video-title-show",
            "--quiet","--verbose=0","--no-color",
            "--no-xlib",
            "--input-fast-seek",
            "--file-caching=1000",
            "--network-caching=2000",
            "--file-logging", $"--logfile={logPath}"
        };
        _lib = new VLC.LibVLC(opts);
        _mp  = new VLC.MediaPlayer(_lib);

        _mp.Playing          += OnPlaying;
        _mp.TimeChanged      += OnTimeChanged;
        _mp.LengthChanged    += OnLengthChanged;
        _mp.EndReached       += OnEndReached;
        _mp.EncounteredError += OnEncounteredError;
        _mp.Stopped          += (_,__) => { lock(_sync){ _stoppedTcs?.TrySetResult(true); _stoppedTcs = null; } };

        _mp.Volume = State.Volume0_100;
        _mp.SetRate((float)State.Speed);

        Log.Debug("LibVLC initialized");
    }
    
    private void OnEndReached(object? _, EventArgs __) {
        lock (_sync) {
            try {
                var len = _mp.Length;
                if (len > 0) {
                    State.Length   = TimeSpan.FromMilliseconds(len);
                    State.Position = TimeSpan.FromMilliseconds(len); // => „am Ende“
                }
                State.IsPlaying = false;
                StateChanged?.Invoke(State);
            } catch (Exception ex) { Log.Debug(ex, "OnEndReached"); }
        }
    }

    private void OnEncounteredError(object? _, EventArgs __) {
        lock (_sync) {
            try {
                State.IsPlaying = false;
                StateChanged?.Invoke(State);
            } catch (Exception ex) { Log.Debug(ex, "OnEncounteredError"); }
        }
    }

    public void Play(string url, long? startMs = null) {
        lock (_sync) {
            _sessionId++;
            var sid = _sessionId;
            Log.Information("[#{sid}] Play {url} (startMs={startMs})", sid, url, startMs);

            // nicht-blockierendes Stop + Media entsorgen
            StopInternalLocked(sid);
            SafeDisposeMediaLocked(sid);

            _pendingSeekMs = (startMs is > 0) ? startMs : null;

            _media = new VLC.Media(_lib, new Uri(url));

            // Startposition als Media-Option (sekundengenau)
            if (_pendingSeekMs is long msOpt && msOpt >= 1000) {
                var secs = (int)(msOpt / 1000);
                _media.AddOption($":start-time={secs}");
                _media.AddOption(":input-fast-seek");
            }

            var started = _mp.Play(_media);
            State.IsPlaying = started;
            StateChanged?.Invoke(State);
            Log.Debug("[#{sid}] started={started}", sid, started);
        }
    }

    
    public void TogglePause() {
        lock (_sync) {
            if (State.IsPlaying) _mp.Pause(); else _mp.Play();
            State.IsPlaying = !State.IsPlaying;
            StateChanged?.Invoke(State);
        }
    }

    public void SeekRelative(TimeSpan delta) {
        lock (_sync) {
            var t = Math.Max(0, _mp.Time + (long)delta.TotalMilliseconds);
            _mp.Time = t;

            // Force immediate UI refresh (do not wait for next VLC tick)
            try {
                State.Position = TimeSpan.FromMilliseconds(t);
                var len = _mp.Length;
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);
                StateChanged?.Invoke(State);
            } catch { /* best-effort */ }
        }
    }

    
    public void SeekTo(TimeSpan position) {
        lock (_sync) {
            var ms = Math.Max(0, (long)position.TotalMilliseconds);
            _mp.Time = ms;

            // Force immediate UI refresh
            try {
                State.Position = TimeSpan.FromMilliseconds(ms);
                var len = _mp.Length;
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);
                StateChanged?.Invoke(State);
            } catch { /* best-effort */ }
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
            if (_mp.IsPlaying) {
                Log.Debug("[#{sid}] Stop()", sid);
                try { _mp.Stop(); } catch (Exception ex) { Log.Debug(ex, "[#{sid}] _mp.Stop threw", sid); }
            }
        } finally {
            _pendingSeekMs = null;
            State.IsPlaying = false;
            StateChanged?.Invoke(State);
        }
    }


    private void SafeDisposeMediaLocked(int sid) {
        try {
            var m = _media; _media = null;
            if (m != null) {
                var t = Task.Run(() => { try { m.Dispose(); } catch (Exception ex) { Log.Debug(ex, "[#{sid}] media.Dispose", sid); } });
                t.Wait(TimeSpan.FromMilliseconds(200));
            }
        } catch (Exception ex) {
            Log.Debug(ex, "[#{sid}] media.Dispose wait", sid);
        }
    }

    private void OnPlaying(object? _, EventArgs __) {
        lock (_sync) {
            var sid = _sessionId;
            var wantMs = _pendingSeekMs;
            Log.Debug("[#{sid}] OnPlaying IsSeekable={seek} len={len} time={t}",
                      sid, _mp.IsSeekable, _mp.Length, _mp.Time);

            if (wantMs is not long ms || ms <= 0) return;

            // 2) Primär: direkt Time setzen (wenn seekbar)
            try {
                if (_mp.IsSeekable) {
                    _mp.Time = ms;
                    Log.Debug("[#{sid}] applied pending seek (time) → {ms}ms", sid, ms);
                } else if (_mp.Length > 0) {
                    // 3) Fallback: Position setzen (0..1), wenn Länge bekannt aber nicht seekbar gemeldet
                    var pos = Math.Clamp((float)ms / _mp.Length, 0f, 1f);
                    _mp.Position = pos;
                    Log.Debug("[#{sid}] applied pending seek (position) → {pos:P0}", sid, pos);
                }
            } catch (Exception ex) {
                Log.Debug(ex, "[#{sid}] pending seek at Playing failed", sid);
            }

            // 4) Zweiter Versuch nach kurzer Zeit (manche Demuxer akzeptieren erst nach ein paar Ticks)
            var retry = ms;
            Task.Run(async () => {
                await Task.Delay(180);
                lock (_sync) {
                    try {
                        if (Math.Abs(_mp.Time - retry) > 800 && (_mp.IsSeekable || _mp.Length > 0)) {
                            if (_mp.IsSeekable) _mp.Time = retry;
                            else _mp.Position = Math.Clamp((float)retry / Math.Max(1, _mp.Length), 0f, 1f);
                            Log.Debug("[#{sid}] applied retry seek", sid);
                        }
                    } catch (Exception ex2) {
                        Log.Debug(ex2, "[#{sid}] retry seek failed", sid);
                    }
                    _pendingSeekMs = null;
                }
            });
        }
    }

    private void OnTimeChanged(object? _, VLC.MediaPlayerTimeChangedEventArgs e) {
        lock (_sync) {
            try {
                State.Position = TimeSpan.FromMilliseconds(e.Time);
                var len = _mp.Length;
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);
                StateChanged?.Invoke(State);
            } catch (Exception ex) {
                Log.Debug(ex, "OnTimeChanged");
            }
        }
    }

    private void OnLengthChanged(object? _, VLC.MediaPlayerLengthChangedEventArgs e) {
        lock (_sync) {
            try {
                State.Length = e.Length > 0 ? TimeSpan.FromMilliseconds(e.Length) : (TimeSpan?)null;
                StateChanged?.Invoke(State);
            } catch (Exception ex) {
                Log.Debug(ex, "OnLengthChanged");
            }
        }
    }

    public void Dispose() {
        lock (_sync) {
            Log.Information("LibVlcPlayer.Dispose");
            try { StopInternalLocked(_sessionId); } catch (Exception ex) { Log.Debug(ex, "StopInternalLocked in Dispose"); }
            SafeDisposeMediaLocked(_sessionId);

            try {
                _mp.Playing       -= OnPlaying;
                _mp.TimeChanged   -= OnTimeChanged;
                _mp.LengthChanged -= OnLengthChanged;
            } catch { }

            try { _mp.Dispose(); } catch (Exception ex) { Log.Debug(ex, "_mp.Dispose"); }
            try { _lib.Dispose(); } catch (Exception ex) { Log.Debug(ex, "_lib.Dispose"); }
        }
    }
}