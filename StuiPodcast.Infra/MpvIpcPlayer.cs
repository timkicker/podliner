using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public sealed class MpvIpcPlayer : IPlayer
{
    public event Action<PlayerState>? StateChanged;

    public PlayerState State { get; } = new() {
        Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
                       PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
                       PlayerCapabilities.Network | PlayerCapabilities.Local
    };

    public string Name => "mpv";

    private readonly object _gate = new();
    private Process? _proc;
    private string _sockPath = "";
    private Timer? _poll;
    private volatile bool _disposed;

    public MpvIpcPlayer()
    {
        // mpv IPC braucht Unix Domain Sockets
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("mpv IPC requires Unix domain sockets (Linux/macOS)");
    }

    public void Play(string url, long? startMs = null)
    {
        Process? started = null;
        string sock;
        lock (_gate)
        {
            StopInternal_NoEvents();
            sock = _sockPath = Path.Combine(Path.GetTempPath(), $"stui-mpv-{Environment.ProcessId}-{Environment.TickCount}.sock");
            try { if (File.Exists(sock)) File.Delete(sock); } catch { /* ignore */ }

            started = StartMpvProcess(url, sock, startMs);
            _proc = started;
            StartPolling(); // will wait for IPC readiness
            State.IsPlaying = true;
        }
        // Events AUßERHALB des Locks
        FireStateChanged();
    }

    public void TogglePause()
    {
        lock (_gate) SendIpc(new { command = new object[] { "cycle", "pause" } }, bestEffort: true);
    }

    public void SeekRelative(TimeSpan delta)
    {
        lock (_gate) SendIpc(new { command = new object[] { "seek", delta.TotalSeconds, "relative" } }, bestEffort: true);
    }

    public void SeekTo(TimeSpan position)
    {
        var sec = position.TotalSeconds < 0 ? 0 : position.TotalSeconds;
        lock (_gate) SendIpc(new { command = new object[] { "seek", sec, "absolute" } }, bestEffort: true);
    }

    public void SetVolume(int vol0to100)
    {
        vol0to100 = Math.Clamp(vol0to100, 0, 100);
        bool raise = false;
        lock (_gate)
        {
            SendIpc(new { command = new object[] { "set_property", "volume", vol0to100 } }, bestEffort: true);
            if (State.Volume0_100 != vol0to100) { State.Volume0_100 = vol0to100; raise = true; }
        }
        if (raise) FireStateChanged();
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(speed, 0.5, 2.5);
        bool raise = false;
        lock (_gate)
        {
            SendIpc(new { command = new object[] { "set_property", "speed", speed } }, bestEffort: true);
            if (Math.Abs(State.Speed - speed) > 0.0001) { State.Speed = speed; raise = true; }
        }
        if (raise) FireStateChanged();
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

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    // ---- intern

    private Process StartMpvProcess(string url, string sockPath, long? startMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mpv",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // ArgumentList vermeidet Quoting-Probleme
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--really-quiet");
        psi.ArgumentList.Add("--terminal=no");
        psi.ArgumentList.Add($"--input-ipc-server={sockPath}");
        psi.ArgumentList.Add("--keep-open=no");
        if (startMs is long ms && ms > 0)
            psi.ArgumentList.Add($"--start={(ms / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
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

    private void StartPolling()
    {
        // leichten Poll starten; der erste Durchlauf wartet auf IPC-Readiness
        _poll = new Timer(async _ => await PollTick(), null, dueTime: 250, period: 500);
    }

    private async Task PollTick()
    {
        if (_disposed) return;
        // IPC-Readiness mit kurzem Backoff
        const int maxWaitMs = 2000;
        var start = Environment.TickCount64;

        // Erstversuch: wenn keine Verbindung möglich, backoff & retry
        while (true)
        {
            if (TryUpdateFromMpv())
            {
                return;
            }
            if (Environment.TickCount64 - start > maxWaitMs) return;
            await Task.Delay(100);
        }
    }

    private bool TryUpdateFromMpv()
    {
        double pos = 0, dur = 0;
        try
        {
            var posVal = GetProperty<double?>("time-pos");
            var durVal = GetProperty<double?>("duration");
            if (posVal is null && durVal is null) return false;

            bool raise = false;
            lock (_gate)
            {
                if (posVal is double p && p >= 0)
                {
                    var ts = TimeSpan.FromSeconds(p);
                    if (ts != State.Position) { State.Position = ts; raise = true; }
                }
                if (durVal is double d && d > 0)
                {
                    var ts = TimeSpan.FromSeconds(d);
                    if (State.Length != ts) { State.Length = ts; raise = true; }
                }
            }
            if (raise) FireStateChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private T? GetProperty<T>(string name)
    {
        var req = new { command = new object[] { "get_property", name } };
        var txt = SendIpc(req, waitResponse: true, bestEffort: true);
        if (txt == null) return default;
        try
        {
            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("data", out var d))
                return System.Text.Json.JsonSerializer.Deserialize<T>(d.GetRawText());
        }
        catch { }
        return default;
    }

    private string? SendIpc(object payload, bool waitResponse = false, bool bestEffort = false)
    {
        string sock;
        lock (_gate) sock = _sockPath;
        if (string.IsNullOrEmpty(sock)) return null;

        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(sock));

            var json = System.Text.Json.JsonSerializer.Serialize(payload) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);
            s.Send(bytes);

            if (!waitResponse) return null;

            s.ReceiveTimeout = 300;
            var buf = new byte[4096];
            var n = s.Receive(buf);
            return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : null;
        }
        catch
        {
            if (bestEffort) return null;
            throw;
        }
    }

    private void StopInternal_NoEvents()
    {
        try { _poll?.Dispose(); } catch { }
        _poll = null;

        try { SendIpc(new { command = new object[] { "quit" } }, bestEffort: true); } catch { }
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;

        var sock = _sockPath;
        _sockPath = "";
        try { if (!string.IsNullOrEmpty(sock) && File.Exists(sock)) File.Delete(sock); } catch { }
    }

    private void FireStateChanged()
    {
        var snapshot = State; // nur Referenz
        try { StateChanged?.Invoke(snapshot); } catch { /* UI-Subscriber sollen App nicht crashen */ }
    }
    public PlayerCapabilities Capabilities => State.Capabilities;
}
