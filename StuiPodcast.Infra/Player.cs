using StuiPodcast.Core;
using VLC = LibVLCSharp.Shared;

namespace StuiPodcast.Infra;

public interface IPlayer {
    event Action<PlayerState>? StateChanged;
    PlayerState State { get; }
    void Play(string url);
    void TogglePause();
    void SeekRelative(TimeSpan delta);
    void SetVolume(int vol0to100);
    void SetSpeed(double speed);
    void Stop();
}

public class LibVlcPlayer : IPlayer, IDisposable {
    private readonly VLC.LibVLC _lib;
    private VLC.MediaPlayer? _mp;
    public PlayerState State { get; } = new();

    public event Action<PlayerState>? StateChanged;

    public LibVlcPlayer() {
        VLC.Core.Initialize();
        _lib = new VLC.LibVLC();
    }

    public void Play(string url) {
        // altes MediaPlayer-Objekt sicher beenden
        SafeDisposeMediaPlayer();

        using var media = new VLC.Media(_lib, new Uri(url));
        _mp = new VLC.MediaPlayer(media);

        _mp.TimeChanged += OnTimeChanged;
        _mp.LengthChanged += OnLengthChanged;

        _mp.Volume = State.Volume0_100;
        _mp.SetRate((float)State.Speed);
        _mp.Play();
        State.IsPlaying = true;
        StateChanged?.Invoke(State);
    }

    public void TogglePause() {
        if (_mp == null) return;
        if (State.IsPlaying) _mp.Pause(); else _mp.Play();
        State.IsPlaying = !State.IsPlaying;
        StateChanged?.Invoke(State);
    }

    public void SeekRelative(TimeSpan delta) {
        if (_mp == null) return;
        var newMs = Math.Max(0, (_mp.Time + (long)delta.TotalMilliseconds));
        _mp.Time = newMs;
    }

    public void SetVolume(int vol0to100) {
        vol0to100 = Math.Clamp(vol0to100, 0, 100);
        State.Volume0_100 = vol0to100;
        if (_mp != null) _mp.Volume = vol0to100;
        StateChanged?.Invoke(State);
    }

    public void SetSpeed(double speed) {
        speed = Math.Clamp(speed, 0.5, 2.5);
        State.Speed = speed;
        if (_mp != null) _mp.SetRate((float)speed);
        StateChanged?.Invoke(State);
    }

    public void Stop() {
        // non-blocking versuchen; wenn’s länger dauert, einfach fallen lassen
        try {
            if (_mp != null) {
                var t = Task.Run(() => { try { _mp.Stop(); } catch { } });
                t.Wait(TimeSpan.FromMilliseconds(200));
            }
        } catch { }
        State.IsPlaying = false;
        StateChanged?.Invoke(State);
    }

    public void Dispose() {
        // Events abhängen, stoppen, freigeben – alles mit Timeouts
        try {
            if (_mp != null) {
                _mp.TimeChanged -= OnTimeChanged;
                _mp.LengthChanged -= OnLengthChanged;
            }
        } catch { }

        try { Stop(); } catch { }

        SafeDisposeMediaPlayer();

        try { _lib?.Dispose(); } catch { }
    }

    private void SafeDisposeMediaPlayer() {
        try {
            if (_mp != null) {
                var local = _mp;
                _mp = null;
                // Dispose in Task, max 200ms warten
                var t = Task.Run(() => { try { local.Dispose(); } catch { } });
                t.Wait(TimeSpan.FromMilliseconds(200));
            }
        } catch { }
    }

    private void OnTimeChanged(object? _, VLC.MediaPlayerTimeChangedEventArgs args) {
        State.Position = TimeSpan.FromMilliseconds(args.Time);
        if (_mp != null && _mp.Length > 0) State.Length = TimeSpan.FromMilliseconds(_mp.Length);
        StateChanged?.Invoke(State);
    }

    private void OnLengthChanged(object? _, VLC.MediaPlayerLengthChangedEventArgs e) {
        State.Length = e.Length > 0 ? TimeSpan.FromMilliseconds(e.Length) : null;
        StateChanged?.Invoke(State);
    }
}
