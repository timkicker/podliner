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
        _mp.Stopped          += OnStopped;

        _mp.Volume = State.Volume0_100;
        _mp.SetRate((float)State.Speed);

        Log.Debug("LibVLC initialized");
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(State);

    public void Play(string url, long? startMs = null) {
        int sid;
        lock (_sync) {
            _sessionId++;
            sid = _sessionId;
            Log.Information("[#{sid}] Play {url} (startMs={startMs})", sid, url, startMs);

            // nicht-blockierendes Stop + Media entsorgen
            StopInternalLocked(sid, raise:false);
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
            Log.Debug("[#{sid}] started={started}", sid, started);
        }
        RaiseStateChanged();
    }

    public void TogglePause() {
        bool raise = false;
        lock (_sync) {
            try {
                if (_mp.CanPause) {
                    _mp.Pause();
                    State.IsPlaying = _mp.IsPlaying; // VLC aktualisiert selbst
                } else {
                    // Fallback: togglen
                    if (State.IsPlaying) _mp.Pause(); else _mp.Play();
                    State.IsPlaying = !State.IsPlaying;
                }
                raise = true;
            } catch (Exception ex) {
                Log.Debug(ex, "TogglePause failed");
            }
        }
        if (raise) RaiseStateChanged();
    }

    public void SeekRelative(TimeSpan delta) {
        bool raise = false;
        lock (_sync) {
            var len = _mp.Length; // -1 oder >=0
            var cur = _mp.Time;
            long target = Math.Max(0, cur + (long)delta.TotalMilliseconds);

            if (len > 0)
                target = Math.Min(target, Math.Max(0, len - 5)); // 5ms Puffer

            try {
                _mp.Time = target;

                // UI-State aktualisieren
                State.Position = TimeSpan.FromMilliseconds(target);
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);

                // Near-End → als „stopped“ melden, damit Auto-Advance triggert
                if (len > 0 && target >= Math.Max(0, len - 250))
                    State.IsPlaying = false;

                raise = true;
            } catch (Exception ex) {
                Log.Debug(ex, "SeekRelative failed");
            }
        }
        if (raise) RaiseStateChanged();
    }

    public void SeekTo(TimeSpan position) {
        bool raise = false;
        lock (_sync) {
            var len = _mp.Length; // -1 oder >=0
            long ms = Math.Max(0, (long)position.TotalMilliseconds);

            if (len > 0)
                ms = Math.Min(ms, Math.Max(0, len - 5)); // nie hinter Ende

            try {
                _mp.Time = ms;

                State.Position = TimeSpan.FromMilliseconds(ms);
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);

                if (len > 0 && ms >= Math.Max(0, len - 250))
                    State.IsPlaying = false;

                raise = true;
            } catch (Exception ex) {
                Log.Debug(ex, "SeekTo failed");
            }
        }
        if (raise) RaiseStateChanged();
    }

    public void SetVolume(int vol0to100) {
        bool raise = false;
        lock (_sync) {
            try {
                vol0to100 = Math.Clamp(vol0to100, 0, 100);
                State.Volume0_100 = vol0to100;
                _mp.Volume = vol0to100;
                raise = true;
            } catch (Exception ex) {
                Log.Debug(ex, "SetVolume failed");
            }
        }
        if (raise) RaiseStateChanged();
    }

    public void SetSpeed(double speed) {
        bool raise = false;
        lock (_sync) {
            try {
                speed = Math.Clamp(speed, 0.5, 2.5);
                State.Speed = speed;
                _mp.SetRate((float)speed);
                raise = true;
            } catch (Exception ex) {
                Log.Debug(ex, "SetSpeed failed");
            }
        }
        if (raise) RaiseStateChanged();
    }

    public void Stop() {
        lock (_sync) {
            StopInternalLocked(_sessionId, raise:false);
        }
        RaiseStateChanged();
    }

    private void StopInternalLocked(int sid, bool raise) {
        try {
            if (_mp.IsPlaying) {
                Log.Debug("[#{sid}] Stop()", sid);
                try { _mp.Stop(); }
                catch (Exception ex) { Log.Debug(ex, "[#{sid}] _mp.Stop threw", sid); }
            }
        } finally {
            _pendingSeekMs = null;
            State.IsPlaying = false;
        }
        if (raise) RaiseStateChanged();
    }

    private void SafeDisposeMediaLocked(int sid) {
        try {
            var m = _media; _media = null;
            if (m != null) {
                var t = Task.Run(() => {
                    try { m.Dispose(); }
                    catch (Exception ex) { Log.Debug(ex, "[#{sid}] media.Dispose", sid); }
                });
                t.Wait(TimeSpan.FromMilliseconds(200));
            }
        } catch (Exception ex) {
            Log.Debug(ex, "[#{sid}] media.Dispose wait", sid);
        }
    }

    private void OnPlaying(object? _, EventArgs __) {
        long? wantMs;
        int sid;
        lock (_sync) {
            sid = _sessionId;
            wantMs = _pendingSeekMs;
            Log.Debug("[#{sid}] OnPlaying IsSeekable={seek} len={len} time={t}",
                      sid, _mp.IsSeekable, _mp.Length, _mp.Time);
        }

        if (wantMs is not long ms || ms <= 0) return;

        try {
            if (_mp.IsSeekable) {
                _mp.Time = ms;
                Log.Debug("[#{sid}] applied pending seek (time) → {ms}ms", sid, ms);
            } else if (_mp.Length > 0) {
                var pos = Math.Clamp((float)ms / _mp.Length, 0f, 1f);
                _mp.Position = pos;
                Log.Debug("[#{sid}] applied pending seek (position) → {pos:P0}", sid, pos);
            }
        } catch (Exception ex) {
            Log.Debug(ex, "[#{sid}] pending seek at Playing failed", sid);
        }

        Task.Run(async () => {
            await Task.Delay(180);
            lock (_sync) {
                try {
                    if (Math.Abs(_mp.Time - ms) > 800 && (_mp.IsSeekable || _mp.Length > 0)) {
                        if (_mp.IsSeekable) _mp.Time = ms;
                        else _mp.Position = Math.Clamp((float)ms / Math.Max(1, _mp.Length), 0f, 1f);
                        Log.Debug("[#{sid}] applied retry seek", sid);
                    }
                } catch (Exception ex2) {
                    Log.Debug(ex2, "[#{sid}] retry seek failed", sid);
                } finally {
                    _pendingSeekMs = null;
                }
            }
        });
    }

    private void OnTimeChanged(object? _, VLC.MediaPlayerTimeChangedEventArgs e) {
        bool raise = false;
        lock (_sync) {
            try {
                State.Position = TimeSpan.FromMilliseconds(e.Time);
                var len = _mp.Length;
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);

                // End-Stall hart signalisieren, falls VLC „stoppt“ ohne Event
                if (len > 0 && e.Time >= Math.Max(0, len - 100))
                    State.IsPlaying = false;

                raise = true;
            } catch (Exception ex) {
                Log.Debug(ex, "OnTimeChanged");
            }
        }
        if (raise) RaiseStateChanged();
    }

    private void OnLengthChanged(object? _, VLC.MediaPlayerLengthChangedEventArgs e) {
        bool raise = false;
        lock (_sync) {
            try {
                State.Length = e.Length > 0 ? TimeSpan.FromMilliseconds(e.Length) : (TimeSpan?)null;
                raise = true;
            } catch (Exception ex) {
                Log.Debug(ex, "OnLengthChanged");
            }
        }
        if (raise) RaiseStateChanged();
    }

    private void OnEndReached(object? _, EventArgs __) {
        bool raise = false;
        lock (_sync) {
            try {
                var len = _mp.Length;
                if (len > 0) {
                    State.Length   = TimeSpan.FromMilliseconds(len);
                    State.Position = TimeSpan.FromMilliseconds(len); // => „am Ende“
                }
                State.IsPlaying = false;
                raise = true;
            } catch (Exception ex) { Log.Debug(ex, "OnEndReached"); }
        }
        if (raise) RaiseStateChanged();
    }

    private void OnEncounteredError(object? _, EventArgs __) {
        bool raise = false;
        lock (_sync) {
            try {
                State.IsPlaying = false;
                raise = true;
            } catch (Exception ex) { Log.Debug(ex, "OnEncounteredError"); }
        }
        if (raise) RaiseStateChanged();
    }

    private void OnStopped(object? _, EventArgs __) {
        bool raise = false;
        lock (_sync) {
            try {
                State.IsPlaying = false;
                raise = true;
            } catch (Exception ex) { Log.Debug(ex, "OnStopped"); }
        }
        if (raise) RaiseStateChanged();
    }

    public void Dispose() {
        lock (_sync) {
            Log.Information("LibVlcPlayer.Dispose");
            try { StopInternalLocked(_sessionId, raise:false); } catch (Exception ex) { Log.Debug(ex, "StopInternalLocked in Dispose"); }
            SafeDisposeMediaLocked(_sessionId);

            try {
                _mp.Playing          -= OnPlaying;
                _mp.TimeChanged      -= OnTimeChanged;
                _mp.LengthChanged    -= OnLengthChanged;
                _mp.EndReached       -= OnEndReached;
                _mp.EncounteredError -= OnEncounteredError;
                _mp.Stopped          -= OnStopped;
            } catch (Exception ex) {
                Log.Debug(ex, "Unsubscribe events in Dispose");
            }

            try { _mp.Dispose(); } catch (Exception ex) { Log.Debug(ex, "_mp.Dispose"); }
            try { _lib.Dispose(); } catch (Exception ex) { Log.Debug(ex, "_lib.Dispose"); }
        }
    }
}
