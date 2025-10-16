using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public sealed class FfplayPlayer : IPlayer
{
    public event Action<PlayerState>? StateChanged;
    public PlayerState State { get; } = new();
    public string Name => "ffplay (limited)";

    // Kein Live-Seek/Speed/Vol (nur Startparameter), Pause/Stop per Prozess – daher schlanker Capability-Satz:
    public PlayerCapabilities Capabilities =>
        PlayerCapabilities.Play | PlayerCapabilities.Stop |
        PlayerCapabilities.Network | PlayerCapabilities.Local;

    private Process? _proc;
    private readonly object _sync = new();

    public FfplayPlayer()
    {
        State.Capabilities = Capabilities;
        // konservative Defaults
        State.Speed = Math.Clamp(State.Speed, 0.5, 2.0);
    }

    public void Play(string url, long? startMs = null)
    {
        lock (_sync)
        {
            StopInternal();

            var args = "";
            // -nodisp: kein Fenster, -autoexit: Prozess endet am Medienende
            // -loglevel quiet: ruhig
            // -ss: Startzeit (vor Input → schneller Seek)
            if (startMs is long ms && ms > 0) args += $" -ss {ms/1000.0:0.###}";
            args += " -nodisp -autoexit -loglevel quiet ";

            // Lautstärke: 0..100 → ffplay erwartet 0..100 (ca.), Speed via atempo (0.5..2.0)
            int vol = Math.Clamp(State.Volume0_100, 0, 100);
            double spd = Math.Clamp(State.Speed, 0.5, 2.0);
            args += $" -volume {vol} -af \"atempo={spd.ToString("0.##", CultureInfo.InvariantCulture)}\" ";

            args += $" \"{url}\"";

            _proc = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "ffplay",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };
            _proc.Exited += (_,__) => { lock (_sync){ State.IsPlaying = false; StateChanged?.Invoke(State);} };
            _proc.Start();

            State.IsPlaying = true;
            StateChanged?.Invoke(State);
        }
    }

    public void TogglePause()
    {
        // Nicht unterstützt – Caller soll Capabilities prüfen und OSD zeigen.
    }

    public void SeekRelative(TimeSpan delta)
    {
        // Nicht live unterstützt → Neu starten ab neuer Position als Best-Effort
        if (State.Length is null) return;
        var newPos = Math.Max(0, (long)State.Position.TotalMilliseconds + (long)delta.TotalMilliseconds);
        SeekTo(TimeSpan.FromMilliseconds(newPos));
    }

    public void SeekTo(TimeSpan position)
    {
        // Best-Effort: Neu starten ab position
        // Quelle erneut spielen (wir kennen die URL nicht hier → kein State), deshalb NOP.
        // Diese Engine ist bewusst „limited“ – CommandRouter soll OSD melden.
    }

    public void SetVolume(int vol0to100)
    {
        // Nur Startparameter, nicht live – aktualisieren wir Lokal, damit UI stimmt:
        State.Volume0_100 = Math.Clamp(vol0to100, 0, 100);
        StateChanged?.Invoke(State);
    }

    public void SetSpeed(double speed)
    {
        State.Speed = Math.Clamp(speed, 0.5, 2.0);
        StateChanged?.Invoke(State);
    }

    public void Stop()
    {
        lock (_sync) StopInternal();
    }

    private void StopInternal()
    {
        try {
            if (_proc != null && !_proc.HasExited) _proc.Kill(true);
        } catch {}
        try { _proc?.Dispose(); } catch {}
        _proc = null;
        State.IsPlaying = false;
        StateChanged?.Invoke(State);
    }

    public void Dispose() => StopInternal();
}
