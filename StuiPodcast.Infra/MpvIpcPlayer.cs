using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public sealed class MpvIpcPlayer : IPlayer
{
    public event Action<PlayerState>? StateChanged;
    public PlayerState State { get; } = new();
    public string Name => "mpv (ipc)";
    public PlayerCapabilities Capabilities =>
        PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
        PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
        PlayerCapabilities.Network | PlayerCapabilities.Local;

    private Process? _proc;
    private string _sockPath = "";
    private readonly object _sync = new();
    private Timer? _pollTimer;

    public MpvIpcPlayer()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("mpv IPC requires Unix domain sockets");

        State.Capabilities = Capabilities;
    }

    public void Play(string url, long? startMs = null)
    {
        lock (_sync)
        {
            StopInternal();

            _sockPath = Path.Combine(Path.GetTempPath(), $"stui-mpv-{Environment.ProcessId}-{Environment.TickCount}.sock");
            try { File.Delete(_sockPath); } catch {}

            var args = new StringBuilder();
            args.Append($"--no-video --really-quiet --terminal=no ");
            args.Append($"--input-ipc-server=\"{_sockPath}\" ");
            if (startMs is long ms && ms > 0) args.Append($" --start={ms/1000.0:0.###}");
            args.Append(" --keep-open=no ");
            args.Append($" \"{url}\"");

            _proc = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "mpv",
                    Arguments = args.ToString(),
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

            // leichter Status-Poll (Position/Länge)
            _pollTimer = new Timer(_ => PollStatus(), null, 500, 500);
        }
    }

    public void TogglePause()
    {
        lock (_sync) SendIpc(new { command = new object[] { "cycle", "pause" } });
    }

    public void SeekRelative(TimeSpan delta)
    {
        lock (_sync) SendIpc(new { command = new object[] { "seek", delta.TotalSeconds, "relative" } });
    }

    public void SeekTo(TimeSpan position)
    {
        lock (_sync) SendIpc(new { command = new object[] { "seek", position.TotalSeconds, "absolute" } });
    }

    public void SetVolume(int vol0to100)
    {
        vol0to100 = Math.Clamp(vol0to100, 0, 100);
        lock (_sync) SendIpc(new { command = new object[] { "set_property", "volume", vol0to100 } });
        State.Volume0_100 = vol0to100;
        StateChanged?.Invoke(State);
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(speed, 0.5, 2.5);
        lock (_sync) SendIpc(new { command = new object[] { "set_property", "speed", speed } });
        State.Speed = speed;
        StateChanged?.Invoke(State);
    }

    public void Stop()
    {
        lock (_sync) StopInternal();
    }

    private void StopInternal()
    {
        try { _pollTimer?.Dispose(); _pollTimer = null; } catch {}
        try { SendIpc(new { command = new object[] { "quit" } }); } catch {}
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch {}
        try { _proc?.Dispose(); } catch {}
        _proc = null;
        State.IsPlaying = false;
        StateChanged?.Invoke(State);
        try { if (!string.IsNullOrEmpty(_sockPath)) File.Delete(_sockPath); } catch {}
    }

    private void PollStatus()
    {
        try {
            // time-pos
            var pos = GetProperty<double?>("time-pos") ?? 0.0;
            var len = GetProperty<double?>("duration") ?? 0.0;

            State.Position = TimeSpan.FromSeconds(Math.Max(0, pos));
            State.Length   = (len > 0.0) ? TimeSpan.FromSeconds(len) : null;
            StateChanged?.Invoke(State);
        } catch { /* best effort */ }
    }

    private T? GetProperty<T>(string name)
    {
        var req = new { command = new object[] { "get_property", name } };
        var txt = SendIpc(req, waitResponse: true);
        if (txt == null) return default;
        try {
            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("data", out var d)) {
                return JsonSerializer.Deserialize<T>(d.GetRawText());
            }
        } catch {}
        return default;
    }

    private string? SendIpc(object payload, bool waitResponse = false)
    {
        if (string.IsNullOrEmpty(_sockPath)) return null;

        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        sock.Connect(new UnixDomainSocketEndPoint(_sockPath));

        var json = JsonSerializer.Serialize(payload) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        sock.Send(bytes);

        if (!waitResponse) return null;

        sock.ReceiveTimeout = 300;
        var buf = new byte[4096];
        int n = 0;
        try { n = sock.Receive(buf); } catch { return null; }
        return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : null;
    }

    public void Dispose() => StopInternal();
}
