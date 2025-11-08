using System.Diagnostics;
using System.Net;
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

    // simple optional logger; stays silent unless env PODLINER_DEBUG_MPV=1
    private static bool DebugEnabled =>
        Environment.GetEnvironmentVariable("PODLINER_DEBUG_MPV") == "1";

    private static void D(string msg)
    {
        if (DebugEnabled)
            try { Trace.WriteLine($"[mpv] {msg}"); } catch { }
    }

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
        }

        TryDeleteFile(sock);

        started = StartMpvProcess(url, sock, startMs);
        lock (_gate) { _proc = started; }

        if (!WaitForIpcReady(sock, totalMs: 500))
            D($"IPC not ready in time: {sock}");

        StartPolling();

        FireStateChanged();
    }

    public void TogglePause()
    {
        bool shouldFire = false;
        lock (_gate)
        {
            if (_ready)
            {
                State.IsPlaying = !State.IsPlaying;
                shouldFire = true;
            }
        }
        if (shouldFire) FireStateChanged();

        SendIpc(new { command = new object[] { "cycle", "pause" } }, waitResponse: false, bestEffort: true);
    }

    public void SeekRelative(TimeSpan delta)
    {
        SendIpc(new { command = new object[] { "seek", delta.TotalSeconds, "relative" } }, waitResponse: false, bestEffort: true);
    }

    public void SeekTo(TimeSpan position)
    {
        var sec = position.TotalSeconds < 0 ? 0 : position.TotalSeconds;
        SendIpc(new { command = new object[] { "seek", sec, "absolute" } }, waitResponse: false, bestEffort: true);
    }

    public void SetVolume(int vol0to100)
    {
        vol0to100 = Math.Clamp(vol0to100, 0, 100);
        bool raise = false;
        lock (_gate)
        {
            if (State.Volume0_100 != vol0to100) { State.Volume0_100 = vol0to100; raise = true; }
        }
        SendIpc(new { command = new object[] { "set_property", "volume", vol0to100 } }, waitResponse: false, bestEffort: true);
        if (raise) FireStateChanged();
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(speed, 0.5, 2.5);
        bool raise = false;
        lock (_gate)
        {
            if (Math.Abs(State.Speed - speed) > 0.0001) { State.Speed = speed; raise = true; }
        }
        SendIpc(new { command = new object[] { "set_property", "speed", speed } }, waitResponse: false, bestEffort: true);
        if (raise) FireStateChanged();
    }

    public void Stop()
    {
        bool raise = false;
        lock (_gate)
        {
            if (_proc != null || !string.IsNullOrEmpty(_sockPath))
            {
                raise = State.IsPlaying;
            }
        }

        StopInternal_NoEvents(); 

        if (raise)
        {
            lock (_gate) { State.IsPlaying = false; }
            FireStateChanged();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    #endregion

    #region helpers: process / start / stop

    private Process StartMpvProcess(string url, string sockPath, long? startMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mpv",
            UseShellExecute = false,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };

        // deterministic, terminal-safe start (no configs, no terminal, no window)
        psi.ArgumentList.Add("--no-config");                 // ignore user configs & scripts
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--no-terminal");               // don't touch TTY
        psi.ArgumentList.Add("--input-terminal=no");         // never read from terminal
        psi.ArgumentList.Add("--input-default-bindings=no"); // disable key bindings
        psi.ArgumentList.Add("--input-conf=/dev/null");      // no input config
        psi.ArgumentList.Add("--force-window=no");           // no GUI window
        psi.ArgumentList.Add("--idle=no");
        psi.ArgumentList.Add("--keep-open=no");
        psi.ArgumentList.Add("--really-quiet");
        psi.ArgumentList.Add("--msg-level=all=no");          // suppress logs to stdio completely
        psi.ArgumentList.Add($"--input-ipc-server={sockPath}");

        // optional start offset hint
        if (startMs is long ms && ms > 0)
            psi.ArgumentList.Add($"--start={(ms / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        psi.ArgumentList.Add(url);

        D($"start mpv: {string.Join(" ", psi.ArgumentList)}");

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, __) =>
        {
            bool raise = false;
            lock (_gate)
            {
                if (State.IsPlaying) { State.IsPlaying = false; raise = true; }
            }
            D("mpv exited");
            if (raise) FireStateChanged();
        };
        p.Start();
        return p;
    }

    private void StartPolling()
    {
        _poll = new Timer(_ => { try { TryUpdateFromMpv(); } catch { } },
                          null, dueTime: 250, period: 500);
    }

    private void StopInternal_NoEvents()
    {
        try { _poll?.Dispose(); } catch { }
        _poll = null;

        Process? proc;
        string sock;
        lock (_gate)
        {
            proc = _proc;
            sock = _sockPath;
        }

        try { SendIpc(new { command = new object[] { "quit" } }, waitResponse: false, bestEffort: true); } catch { }

        try
        {
            if (proc is { HasExited: false })
            {
                if (!proc.WaitForExit(250))
                {
                    D("mpv still running after 250ms, killing...");
                    try { proc.Kill(true); } catch { }
                }
            }
        }
        catch { }

        try { proc?.Dispose(); } catch { }

        lock (_gate)
        {
            _proc = null;
            _sockPath = "";
            _ready = false;
        }

        TryDeleteFile(sock);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                D($"unlink: {path}");
            }
        }
        catch { }
    }

    private static bool WaitForIpcReady(string sock, int totalMs)
    {
        if (string.IsNullOrEmpty(sock)) return false;
        var deadline = Environment.TickCount64 + totalMs;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                {
                    ReceiveTimeout = 100,
                    SendTimeout = 100
                };
                s.Connect(new UnixDomainSocketEndPoint(sock));
                return true;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    #endregion

    #region helpers: state polling

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

    #endregion

    #region helpers: ipc

    private string? SendIpc(object payload, bool waitResponse = false, bool bestEffort = false)
    {
        string sock;
        lock (_gate) sock = _sockPath;
        if (string.IsNullOrEmpty(sock)) return null;

        const int connectTimeoutMs = 200;
        const int retries = 2;

        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.ReceiveTimeout = 300;
                s.SendTimeout = 300;

                if (!ConnectWithTimeout(s, new UnixDomainSocketEndPoint(sock), connectTimeoutMs))
                    throw new SocketException((int)SocketError.TimedOut);

                var json = JsonSerializer.Serialize(payload) + "\n";
                var bytes = Encoding.UTF8.GetBytes(json);
                s.Send(bytes);

                if (!waitResponse) return null;

                var buf = new byte[4096];
                var n = s.Receive(buf);
                return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : null;
            }
            catch (SocketException se)
            {
                D($"ipc connect/send failed ({se.SocketErrorCode}), attempt {attempt + 1}/{retries} sock={sock}");
                if (attempt + 1 < retries)
                {
                    TryDeleteFile(sock);
                    Thread.Sleep(50);
                    continue;
                }
                if (bestEffort) return null;
                throw;
            }
            catch (Exception ex)
            {
                D($"ipc error: {ex.GetType().Name}: {ex.Message}");
                if (bestEffort) return null;
                throw;
            }
        }
        return null;
    }

    private static bool ConnectWithTimeout(Socket s, EndPoint ep, int timeoutMs)
    {
        try
        {
            var t = s.ConnectAsync(ep);
            if (t.Wait(timeoutMs)) return true;
            try { s.Close(); } catch { }
            return false;
        }
        catch
        {
            try { s.Close(); } catch { }
            throw;
        }
    }

    #endregion

    #region helpers: ui

    private void FireStateChanged()
    {
        var snapshot = State;
        try { StateChanged?.Invoke(snapshot); } catch { }
    }

    #endregion
}

