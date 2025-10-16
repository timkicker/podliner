using Serilog;
using StuiPodcast.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using VLC = LibVLCSharp.Shared;

namespace StuiPodcast.Infra;

public interface IPlayer : IDisposable {
    event Action<PlayerState>? StateChanged;
    PlayerState State { get; }
    string Name { get; }                  // neu: Anzeigename der Engine
    PlayerCapabilities Capabilities { get; } // neu: Fähigkeiten

    void Play(string url, long? startMs = null);
    void TogglePause();
    void SeekRelative(TimeSpan delta);
    void SeekTo(TimeSpan position);
    void SetVolume(int vol0to100);
    void SetSpeed(double speed);
    void Stop();
}

public sealed class LibVlcPlayer : IPlayer {
    private readonly VLC.LibVLC _lib;
    private readonly VLC.MediaPlayer _mp;
    private VLC.Media? _media;
    private readonly object _sync = new();
    private long? _pendingSeekMs;
    private int _sessionId;

    public string Name => "libVLC";
    public PlayerCapabilities Capabilities =>
        PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
        PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
        PlayerCapabilities.Network | PlayerCapabilities.Local;

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
        _mp.Stopped          += (_,__) => { lock(_sync){ State.IsPlaying = false; StateChanged?.Invoke(State);} };

        _mp.Volume = State.Volume0_100;
        _mp.SetRate((float)State.Speed);

        State.Capabilities = Capabilities;
        Log.Debug("LibVLC initialized");
    }

    public void Play(string url, long? startMs = null) {
        lock (_sync) {
            _sessionId++;
            var sid = _sessionId;
            Log.Information("[#{sid}] Play {url} (startMs={startMs})", sid, url, startMs);

            try { if (_mp.IsPlaying) _mp.Stop(); } catch {}
            SafeDisposeMediaLocked(sid);

            _pendingSeekMs = (startMs is > 0) ? startMs : null;

            _media = new VLC.Media(_lib, new Uri(url));

            if (_pendingSeekMs is long msOpt && msOpt >= 1000) {
                var secs = (int)(msOpt / 1000);
                _media.AddOption($":start-time={secs}");
                _media.AddOption(":input-fast-seek");
            }

            var ok = _mp.Play(_media);
            State.IsPlaying = ok;
            State.Capabilities = Capabilities;
            StateChanged?.Invoke(State);
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
            var len = _mp.Length;
            var cur = _mp.Time;
            long target = Math.Max(0, cur + (long)delta.TotalMilliseconds);
            if (len > 0) target = Math.Min(target, Math.Max(0, len - 5));
            _mp.Time = target;
            try {
                State.Position = TimeSpan.FromMilliseconds(target);
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);
                if (len > 0 && target >= Math.Max(0, len - 250)) State.IsPlaying = false;
                StateChanged?.Invoke(State);
            } catch {}
        }
    }

    public void SeekTo(TimeSpan position) {
        lock (_sync) {
            var len = _mp.Length;
            long ms = Math.Max(0, (long)position.TotalMilliseconds);
            if (len > 0) ms = Math.Min(ms, Math.Max(0, len - 5));
            _mp.Time = ms;
            try {
                State.Position = TimeSpan.FromMilliseconds(ms);
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);
                if (len > 0 && ms >= Math.Max(0, len - 250)) State.IsPlaying = false;
                StateChanged?.Invoke(State);
            } catch {}
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
        lock (_sync) {
            try { if (_mp.IsPlaying) _mp.Stop(); } catch {}
            State.IsPlaying = false;
            StateChanged?.Invoke(State);
        }
    }

    private void SafeDisposeMediaLocked(int sid) {
        try {
            var m = _media; _media = null;
            m?.Dispose();
        } catch (Exception ex) {
            Log.Debug(ex, "[#{sid}] media.Dispose");
        }
    }

    private void OnPlaying(object? _, EventArgs __) {
        lock (_sync) {
            var want = _pendingSeekMs;
            _pendingSeekMs = null;
            if (want is long ms && ms > 0) {
                try {
                    if (_mp.IsSeekable) _mp.Time = ms;
                    else if (_mp.Length > 0) _mp.Position = Math.Clamp((float)ms / _mp.Length, 0f, 1f);
                } catch (Exception ex) { Log.Debug(ex, "pending seek failed"); }
            }
        }
    }

    private void OnTimeChanged(object? _, VLC.MediaPlayerTimeChangedEventArgs e) {
        lock (_sync) {
            try {
                State.Position = TimeSpan.FromMilliseconds(e.Time);
                var len = _mp.Length;
                if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);
                if (len > 0 && e.Time >= Math.Max(0, len - 100)) State.IsPlaying = false;
                StateChanged?.Invoke(State);
            } catch (Exception ex) { Log.Debug(ex, "OnTimeChanged"); }
        }
    }

    private void OnLengthChanged(object? _, VLC.MediaPlayerLengthChangedEventArgs e) {
        lock (_sync) {
            try {
                State.Length = e.Length > 0 ? TimeSpan.FromMilliseconds(e.Length) : (TimeSpan?)null;
                StateChanged?.Invoke(State);
            } catch (Exception ex) { Log.Debug(ex, "OnLengthChanged"); }
        }
    }

    private void OnEndReached(object? _, EventArgs __) {
        lock (_sync) {
            try {
                var len = _mp.Length;
                if (len > 0) {
                    State.Length   = TimeSpan.FromMilliseconds(len);
                    State.Position = TimeSpan.FromMilliseconds(len);
                }
                State.IsPlaying = false;
                StateChanged?.Invoke(State);
            } catch (Exception ex) { Log.Debug(ex, "OnEndReached"); }
        }
    }

    private void OnEncounteredError(object? _, EventArgs __) {
        lock (_sync) {
            try { State.IsPlaying = false; StateChanged?.Invoke(State); } catch (Exception ex) { Log.Debug(ex, "OnEncounteredError"); }
        }
    }

    public void Dispose() {
        lock (_sync) {
            try {
                _mp.Playing       -= OnPlaying;
                _mp.TimeChanged   -= OnTimeChanged;
                _mp.LengthChanged -= OnLengthChanged;
                _mp.EndReached    -= OnEndReached;
                _mp.EncounteredError -= OnEncounteredError;
            } catch {}
            try { _mp.Dispose(); } catch {}
            try { _lib.Dispose(); } catch {}
        }
    }
}
