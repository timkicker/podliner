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

namespace StuiPodcast.Infra.Player;

public sealed class MpvIpcPlayer : IPlayer
{
    public event Action<PlayerState>? StateChanged;

    public PlayerState State { get; } = new()
    {
        Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
                       PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
                       PlayerCapabilities.Network | PlayerCapabilities.Local
    };

    public string Name => "mpv";
    public PlayerCapabilities Capabilities => State.Capabilities;

    private readonly object _gate = new();
    private Process? _proc;
    private string _sockPath = "";
    private Timer? _poll;
    private volatile bool _disposed;

    // Ready-Gate: Erst wenn wir das erste Mal valide time-pos oder duration erhalten haben,
    // wird IsPlaying = true gesetzt und ein StateChanged gefeuert.
    private bool _ready;

    public MpvIpcPlayer()
    {
        // mpv IPC braucht Unix Domain Sockets (Linux/macOS)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("mpv IPC requires Unix domain sockets (Linux/macOS)");
    }

    public void Play(string url, long? startMs = null)
    {
        Process? started;
        string sock;

        lock (_gate)
        {
            StopInternal_NoEvents();

            _ready = false;              // neue Session: noch nicht "ready"
            State.IsPlaying = false;     // nicht optimistisch setzen
            State.Position = TimeSpan.Zero;
            State.Length = null;

            sock = _sockPath = Path.Combine(
                Path.GetTempPath(), $"podliner-mpv-{Environment.ProcessId}-{Environment.TickCount}.sock");

            try { if (File.Exists(sock)) File.Delete(sock); } catch { /* ignore */ }

            started = StartMpvProcess(url, sock, startMs);
            _proc = started;

            StartPolling(); // Timer, der regelmäßig TryUpdateFromMpv() aufruft
        }

        // Ein initiales StateChanged ist ok (UI kann darauf Loading anzeigen),
        // aber ohne IsPlaying=true – das kommt erst nach dem ersten IPC-Read.
        FireStateChanged();
    }

    public void TogglePause()
    {
        lock (_gate)
            SendIpc(new { command = new object[] { "cycle", "pause" } }, bestEffort: true);
    }

    public void SeekRelative(TimeSpan delta)
    {
        lock (_gate)
            SendIpc(new { command = new object[] { "seek", delta.TotalSeconds, "relative" } }, bestEffort: true);
    }

    public void SeekTo(TimeSpan position)
    {
        var sec = position.TotalSeconds < 0 ? 0 : position.TotalSeconds;
        lock (_gate)
            SendIpc(new { command = new object[] { "seek", sec, "absolute" } }, bestEffort: true);
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
            if (_proc != null || !string.IsNullOrEmpty(_sockPath))
            {
                StopInternal_NoEvents();
                if (State.IsPlaying) { State.IsPlaying = false; raise = true; }
            }
        }
        if (raise) FireStateChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    // ---- intern ---------------------------------------------------------------

    private Process StartMpvProcess(string url, string sockPath, long? startMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mpv",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        // Basis-Argumente (deterministischer, ruhiger Start)
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--really-quiet");
        psi.ArgumentList.Add("--terminal=no");
        psi.ArgumentList.Add($"--input-ipc-server={sockPath}");
        psi.ArgumentList.Add("--keep-open=no");

        // Startoffset (nur als Hint; Coordinator resumiert ggf. später einmalig)
        if (startMs is long ms && ms > 0)
            psi.ArgumentList.Add($"--start={(ms / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        psi.ArgumentList.Add(url);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, __) =>
        {
            bool raise = false;
            lock (_gate)
            {
                // Bei Exit sofort auf nicht spielend
                if (State.IsPlaying) { State.IsPlaying = false; raise = true; }
            }
            if (raise) FireStateChanged();
        };
        p.Start();
        return p;
    }

    private void StartPolling()
    {
        // leichte Poll-Periode; der Timer ruft TryUpdateFromMpv() regelmäßig auf
        // erster Tick nach kurzer Verzögerung, damit mpv IPC-Socket anlegt
        _poll = new Timer(_ => { try { TryUpdateFromMpv(); } catch { /* swallow */ } },
                          null, dueTime: 250, period: 500);
    }

    /// <summary>
    /// Liest time-pos und duration via IPC und aktualisiert State.
    /// Beim *ersten* erfolgreichen Read wird State.IsPlaying=true gesetzt (Ready-Gate).
    /// </summary>
    private bool TryUpdateFromMpv()
    {
        if (_disposed) return false;

        double? posVal = null, durVal = null;
        try
        {
            posVal = GetProperty<double?>("time-pos");
            durVal = GetProperty<double?>("duration");

            if (posVal is null && durVal is null)
                return false;

            bool raise = false;
            lock (_gate)
            {
                // Position
                if (posVal is double p && p >= 0)
                {
                    var ts = TimeSpan.FromSeconds(p);
                    if (ts != State.Position) { State.Position = ts; raise = true; }
                }

                // Länge
                if (durVal is double d && d > 0)
                {
                    var ts = TimeSpan.FromSeconds(d);
                    if (State.Length != ts) { State.Length = ts; raise = true; }
                }

                // Ready-Gate: Erst jetzt "spielend"
                if (!_ready && (posVal is double pp && pp > 0 || durVal is double dd && dd > 0))
                {
                    _ready = true;
                    if (!State.IsPlaying) { State.IsPlaying = true; raise = true; }
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
                return JsonSerializer.Deserialize<T>(d.GetRawText());
        }
        catch { /* ignore */ }
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

            var json = JsonSerializer.Serialize(payload) + "\n";
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

        _ready = false;
    }

    private void FireStateChanged()
    {
        var snapshot = State; // nur Referenz
        try { StateChanged?.Invoke(snapshot); } catch { /* UI-Subscriber sollen App nicht crashen */ }
    }
}
