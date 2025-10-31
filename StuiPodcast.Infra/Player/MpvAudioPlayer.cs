using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Player;

public sealed class MpvAudioPlayer : IAudioPlayer
{
    #region fields and ctor

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

    // ready gate: set isplaying to true only after first valid time or duration update
    private bool _ready;

    public MpvAudioPlayer()
    {
        // mpv ipc needs unix domain sockets on linux and macos
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("mpv IPC requires Unix domain sockets (Linux/macOS)");
    }

    #endregion

    #region public api

    public void Play(string url, long? startMs = null)
    {
        Process? started;
        string sock;

        lock (_gate)
        {
            StopInternal_NoEvents();

            _ready = false;              // new session not ready yet
            State.IsPlaying = false;     // do not set optimistically
            State.Position = TimeSpan.Zero;
            State.Length = null;

            sock = _sockPath = Path.Combine(
                Path.GetTempPath(), $"podliner-mpv-{Environment.ProcessId}-{Environment.TickCount}.sock");

            try { if (File.Exists(sock)) File.Delete(sock); } catch { }

            started = StartMpvProcess(url, sock, startMs);
            _proc = started;

            StartPolling(); // timer that calls tryupdatefrommpv regularly
        }

        // fire initial state change without isplaying true so ui can show loading
        FireStateChanged();
    }

    public void TogglePause()
    {
        lock (_gate)
        {
            // Optimistically toggle the playing state immediately for responsive UI
            if (_ready)
            {
                State.IsPlaying = !State.IsPlaying;
                FireStateChanged();
            }
            // Send the command to mpv
            SendIpc(new { command = new object[] { "cycle", "pause" } }, bestEffort: true);
        }
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

    #endregion

    #region helpers

    private Process StartMpvProcess(string url, string sockPath, long? startMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mpv",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        // base args for quiet deterministic start
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--really-quiet");
        psi.ArgumentList.Add("--terminal=no");
        psi.ArgumentList.Add($"--input-ipc-server={sockPath}");
        psi.ArgumentList.Add("--keep-open=no");

        // optional start offset hint
        if (startMs is long ms && ms > 0)
            psi.ArgumentList.Add($"--start={(ms / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        psi.ArgumentList.Add(url);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, __) =>
        {
            bool raise = false;
            lock (_gate)
            {
                // on exit set not playing immediately
                if (State.IsPlaying) { State.IsPlaying = false; raise = true; }
            }
            if (raise) FireStateChanged();
        };
        p.Start();
        return p;
    }

    private void StartPolling()
    {
        // light polling period; first tick after short delay so socket exists
        _poll = new Timer(_ => { try { TryUpdateFromMpv(); } catch { } },
                          null, dueTime: 250, period: 500);
    }

    // read time and duration via ipc and update state
    // on first successful read switch to playing via the ready gate
    private bool TryUpdateFromMpv()
    {
        if (_disposed) return false;

        double? posVal = null, durVal = null;
        bool? pauseVal = null;
        try
        {
            posVal = GetProperty<double?>("time-pos");
            durVal = GetProperty<double?>("duration");
            pauseVal = GetProperty<bool?>("pause");

            if (posVal is null && durVal is null && pauseVal is null)
                return false;

            bool raise = false;
            lock (_gate)
            {
                // position
                if (posVal is double p && p >= 0)
                {
                    var ts = TimeSpan.FromSeconds(p);
                    if (ts != State.Position) { State.Position = ts; raise = true; }
                }

                // length
                if (durVal is double d && d > 0)
                {
                    var ts = TimeSpan.FromSeconds(d);
                    if (State.Length != ts) { State.Length = ts; raise = true; }
                }

                // pause state
                if (pauseVal is bool paused)
                {
                    var isPlaying = !paused;
                    if (_ready && State.IsPlaying != isPlaying) { State.IsPlaying = isPlaying; raise = true; }
                    // If not ready yet, use pause to determine initial playing state
                    else if (!_ready && !paused && (posVal is double || durVal is double))
                    {
                        _ready = true;
                        if (!State.IsPlaying) { State.IsPlaying = true; raise = true; }
                    }
                }

                // ready gate now playing (fallback if no pause info)
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
        var snapshot = State;
        try { StateChanged?.Invoke(snapshot); } catch { }
    }

    #endregion
}
