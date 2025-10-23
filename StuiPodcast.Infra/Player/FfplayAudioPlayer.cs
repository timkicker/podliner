using System;
using System.Diagnostics;
using System.Globalization;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Player;

// Minimal-Fallback: keine Live-Pause/Seek/Speed/Volume – nur via Neustart.
// Sichere Argument-Übergabe (ArgumentList) & Event-Dispatch außerhalb Locks.
public sealed class FfplayAudioPlayer : IPlayer
{
    public PlayerCapabilities Capabilities => State.Capabilities;
    public event Action<PlayerState>? StateChanged;

    public PlayerState State { get; } = new()
    {
        Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Stop |
                       PlayerCapabilities.Network | PlayerCapabilities.Local
    };

    public string Name => "ffplay";

    private readonly object _gate = new();
    private Process? _proc;
    private string? _lastUrl;
    private long _lastStartMs;

    public void Play(string url, long? startMs = null)
    {
        lock (_gate)
        {
            StopInternal_NoEvents();
            _lastUrl = url;
            _lastStartMs = Math.Max(0, startMs ?? 0);
            _proc = StartFfplay(url, _lastStartMs, State.Volume0_100, State.Speed);
            State.IsPlaying = true;
        }
        FireStateChanged();
    }

    public void TogglePause() { /* not supported */ }

    public void SeekRelative(TimeSpan delta)
        => SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, State.Position.TotalMilliseconds + delta.TotalMilliseconds)));

    public void SeekTo(TimeSpan position)
    {
        Process? started = null;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_lastUrl)) return;
            var ms = Math.Max(0, (long)position.TotalMilliseconds);
            _lastStartMs = ms;
            StopInternal_NoEvents();
            started = StartFfplay(_lastUrl!, _lastStartMs, State.Volume0_100, State.Speed);
            _proc = started;
            State.IsPlaying = true;
            // UI-Schätzung (ffplay liefert keine Position)
            State.Position = TimeSpan.FromMilliseconds(ms);
        }
        FireStateChanged();
    }

    public void SetVolume(int vol0to100)
    {
        bool raise = false;
        lock (_gate)
        {
            var v = Math.Clamp(vol0to100, 0, 100);
            if (v != State.Volume0_100) { State.Volume0_100 = v; raise = true; }
        }
        if (raise) FireStateChanged(); // wirksam erst beim nächsten Start
    }

    public void SetSpeed(double speed)
    {
        bool raise = false;
        lock (_gate)
        {
            var s = Math.Clamp(speed, 0.5, 2.0);
            if (Math.Abs(State.Speed - s) > 0.0001) { State.Speed = s; raise = true; }
        }
        if (raise) FireStateChanged(); // wirksam erst beim nächsten Start
    }

    public void Stop()
    {
        bool raise = false;
        lock (_gate)
        {
            if (State.IsPlaying) { StopInternal_NoEvents(); State.IsPlaying = false; raise = true; }
        }
        if (raise) FireStateChanged();
    }

    public void Dispose() => Stop();

    // ---- intern

    private Process StartFfplay(string url, long startMs, int vol0to100, double speed)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffplay",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        if (startMs > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add((startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-nodisp");
        psi.ArgumentList.Add("-autoexit");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-volume"); psi.ArgumentList.Add(Math.Clamp(vol0to100, 0, 100).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-af"); psi.ArgumentList.Add($"atempo={Math.Clamp(speed, 0.5, 2.0).ToString("0.##", CultureInfo.InvariantCulture)}");
        psi.ArgumentList.Add(url);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, __) =>
        {
            bool raise = false;
            lock (_gate)
            {
                if (State.IsPlaying) { State.IsPlaying = false; raise = true; }
            }
            if (raise) FireStateChanged();
        };
        p.Start();
        return p;
    }

    private void StopInternal_NoEvents()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    private void FireStateChanged()
    {
        var snapshot = State;
        try { StateChanged?.Invoke(snapshot); } catch { /* swallow */ }
    }
}
