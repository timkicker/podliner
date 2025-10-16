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
    string Name { get; }                      // Anzeigename der Engine
    PlayerCapabilities Capabilities { get; }  // Fähigkeiten

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

    // Für :engine show/OSD etc. kurz & konsistent halten
    public string Name => "vlc";

    public PlayerCapabilities Capabilities =>
        PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
        PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
        PlayerCapabilities.Network | PlayerCapabilities.Local;

    public PlayerState State { get; } = new();
    public event Action<PlayerState>? StateChanged;

    public LibVlcPlayer() {
        // 0) Best-effort Pfadfindung für Windows/macOS (Linux meist no-op)
        var vlcPaths = VlcPathResolver.Apply();

        // 1) Native Lib laden (muss NACH Env-Anpassungen erfolgen)
        VLC.Core.Initialize();

        // 2) Logging-Datei vorbereiten (optional, hilft bei Support)
        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "stui-vlc.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }

        // 3) Runtime-Optionen (unsere Defaults + evtl. plugin-path vom Resolver)
        var baseOpts = new[]{
            "--no-video-title-show",
            "--quiet","--verbose=0","--no-color",
            "--no-xlib",                 // headless-freundlich
            "--input-fast-seek",
            "--file-caching=1000",
            "--network-caching=2000",
            "--file-logging", $"--logfile={logPath}"
        };
        var opts = MergeOpts(baseOpts, vlcPaths.LibVlcOptions);

        // 4) Lib & Player anlegen
        _lib = new VLC.LibVLC(opts);
        _mp  = new VLC.MediaPlayer(_lib);

        // 5) Events (UI rendert über Snapshot; hier nur State pflegen + feuern)
        _mp.Playing          += OnPlaying;
        _mp.TimeChanged      += OnTimeChanged;
        _mp.LengthChanged    += OnLengthChanged;
        _mp.EndReached       += OnEndReached;
        _mp.EncounteredError += OnEncounteredError;
        _mp.Stopped          += (_,__) => { lock(_sync){ State.IsPlaying = false; SafeFire(); } };

        // 6) Startzustand
        _mp.Volume = State.Volume0_100;
        _mp.SetRate((float)State.Speed);
        State.Capabilities = Capabilities;

        Log.Information("LibVLC initialized ({Diag})", vlcPaths.Diagnose);
    }

    public void Play(string url, long? startMs = null) {
        lock (_sync) {
            _sessionId++;
            var sid = _sessionId;
            Log.Information("[#{sid}] Play {url} (startMs={startMs})", sid, url, startMs);

            try { if (_mp.IsPlaying) _mp.Stop(); } catch {}
            SafeDisposeMediaLocked(sid);

            _pendingSeekMs = (startMs is > 0) ? startMs : null;

            // URL vs. lokaler Pfad robust unterscheiden
            _media = CreateMedia(_lib, url);

            // Startoffset als Hint mitgeben (beschleunigt initial seek auf manchen Inputs)
            if (_pendingSeekMs is long msOpt && msOpt >= 1000) {
                var secs = (int)(msOpt / 1000);
                _media.AddOption($":start-time={secs}");
                _media.AddOption(":input-fast-seek");
            }

            var ok = _mp.Play(_media);
            State.IsPlaying = ok;
            State.Capabilities = Capabilities;
            SafeFire();
        }
    }

    public void TogglePause() {
        lock (_sync) {
            if (State.IsPlaying) _mp.Pause(); else _mp.Play();
            State.IsPlaying = !State.IsPlaying;
            SafeFire();
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
                SafeFire();
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
                SafeFire();
            } catch {}
        }
    }

    public void SetVolume(int vol0to100) {
        lock (_sync) {
            vol0to100 = Math.Clamp(vol0to100, 0, 100);
            State.Volume0_100 = vol0to100;
            _mp.Volume = vol0to100;
            SafeFire();
        }
    }

    public void SetSpeed(double speed) {
        lock (_sync) {
            speed = Math.Clamp(speed, 0.5, 2.5);
            State.Speed = speed;
            _mp.SetRate((float)speed);
            SafeFire();
        }
    }

    public void Stop() {
        lock (_sync) {
            try { if (_mp.IsPlaying) _mp.Stop(); } catch {}
            State.IsPlaying = false;
            SafeFire();
        }
    }

    // -------------------- Internals --------------------

    private static VLC.Media CreateMedia(VLC.LibVLC lib, string input)
    {
        // 1) Gültige absolute URI?
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            // file:// → Pfad
            if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                return new VLC.Media(lib, uri.LocalPath, VLC.FromType.FromPath);

            // http(s)/… → Location
            return new VLC.Media(lib, uri.ToString(), VLC.FromType.FromLocation);
        }

        // 2) Andernfalls evtl. lokaler Pfad?
        if (File.Exists(input))
            return new VLC.Media(lib, input, VLC.FromType.FromPath);

        // 3) Fallback: als Location versuchen (VLC ist tolerant)
        return new VLC.Media(lib, input, VLC.FromType.FromLocation);
    }

    private void SafeDisposeMediaLocked(int sid) {
        try { _media?.Dispose(); } catch (Exception ex) { Log.Debug(ex, "[#{sid}] media.Dispose"); }
        _media = null;
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
                SafeFire();
            } catch (Exception ex) { Log.Debug(ex, "OnTimeChanged"); }
        }
    }

    private void OnLengthChanged(object? _, VLC.MediaPlayerLengthChangedEventArgs e) {
        lock (_sync) {
            try {
                State.Length = e.Length > 0 ? TimeSpan.FromMilliseconds(e.Length) : (TimeSpan?)null;
                SafeFire();
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
                SafeFire();
            } catch (Exception ex) { Log.Debug(ex, "OnEndReached"); }
        }
    }

    private void OnEncounteredError(object? _, EventArgs __) {
        lock (_sync) {
            try { State.IsPlaying = false; SafeFire(); } catch (Exception ex) { Log.Debug(ex, "OnEncounteredError"); }
        }
    }

    private void SafeFire()
    {
        try { StateChanged?.Invoke(State); } catch { /* UI darf Player nicht crashen */ }
    }

    private static string[] MergeOpts(string[] a, string[] b)
    {
        if (a == null || a.Length == 0) return b ?? Array.Empty<string>();
        if (b == null || b.Length == 0) return a;
        var res = new string[a.Length + b.Length];
        Array.Copy(a, 0, res, 0, a.Length);
        Array.Copy(b, 0, res, a.Length, b.Length);
        return res;
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
