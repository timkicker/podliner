using System;
using System.Diagnostics;
using System.Globalization;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public sealed class FfplayPlayer : IPlayer
{
    public event Action<PlayerState>? StateChanged;
    public PlayerState State { get; } = new();
    public string Name => "ffplay (limited)";

    public PlayerCapabilities Capabilities =>
        PlayerCapabilities.Play | PlayerCapabilities.Stop |
        PlayerCapabilities.Network | PlayerCapabilities.Local;
    // (Kein Live-Pause/Seek/Speed/Volume – nur per Neustart)

    private Process? _proc;
    private readonly object _sync = new();

    private string? _lastUrl;
    private long _lastStartMs = 0;

    public FfplayPlayer()
    {
        State.Capabilities = Capabilities;
        State.Speed = Math.Clamp(State.Speed, 0.5, 2.0);
    }

    public void Play(string url, long? startMs = null)
    {
        lock (_sync)
        {
            StopInternal();
            _lastUrl = url;
            _lastStartMs = Math.Max(0, startMs ?? 0);
            StartProcess(url, _lastStartMs, State.Volume0_100, State.Speed);

            State.IsPlaying = true;
            StateChanged?.Invoke(State);
        }
    }

    public void TogglePause() { /* nicht unterstützt */ }

    public void SeekRelative(TimeSpan delta)
    {
        var posMs = (long)Math.Max(0, State.Position.TotalMilliseconds);
        SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, posMs + (long)delta.TotalMilliseconds)));
    }

    public void SeekTo(TimeSpan position)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_lastUrl)) return;
            var ms = Math.Max(0, (long)position.TotalMilliseconds);
            _lastStartMs = ms;
            try
            {
                StopInternal();
                StartProcess(_lastUrl!, _lastStartMs, State.Volume0_100, State.Speed);
                State.IsPlaying = true;
                State.Position = TimeSpan.FromMilliseconds(ms); // grobe UI-Schätzung
                StateChanged?.Invoke(State);
            }
            catch (Exception ex) { Log.Debug(ex, "ffplay coarse seek restart failed"); }
        }
    }

    public void SetVolume(int vol0to100)
    {
        State.Volume0_100 = Math.Clamp(vol0to100, 0, 100); // erst beim nächsten Start wirksam
        StateChanged?.Invoke(State);
    }

    public void SetSpeed(double speed)
    {
        State.Speed = Math.Clamp(speed, 0.5, 2.0); // erst beim nächsten Start wirksam
        StateChanged?.Invoke(State);
    }

    public void Stop() { lock (_sync) StopInternal(); }

    public void Dispose() => StopInternal();

    // intern
    private void StartProcess(string url, long startMs, int vol0to100, double speed)
    {
        var args = "";
        if (startMs > 0) args += $" -ss {startMs / 1000.0:0.###}";
        args += " -nodisp -autoexit -loglevel quiet ";

        int vol = Math.Clamp(vol0to100, 0, 100);
        double spd = Math.Clamp(speed, 0.5, 2.0);
        args += $" -volume {vol} -af \"atempo={spd.ToString("0.##", CultureInfo.InvariantCulture)}\" ";
        args += $" \"{url}\"";

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffplay",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };
        _proc.Exited += (_, __) =>
        {
            lock (_sync)
            {
                State.IsPlaying = false;
                StateChanged?.Invoke(State);
            }
        };
        _proc.Start();
    }

    private void StopInternal()
    {
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        State.IsPlaying = false;
        StateChanged?.Invoke(State);
    }
}
