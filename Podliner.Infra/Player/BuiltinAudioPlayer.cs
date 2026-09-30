using Podliner.Core;
using Serilog;
using SoundFlow.Abstracts;
using SfEngine = SoundFlow.Abstracts.AudioEngine;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Structs;

namespace Podliner.Infra.Player;

// An engine that needs nothing installed (#3): decoding and output run in
// process through SoundFlow and miniaudio, which bring their own native
// library for every platform podliner ships. Written for macOS, which had no
// fallback when VLC and mpv were missing, and selectable everywhere with
// :engine builtin. mp3 only for now; that is what nearly every feed serves.
//
// Playback runs in segments. A segment starts at a byte of the file and
// decodes on from there; a jump more than a few seconds ahead, or any jump
// back, starts a new segment at the byte Mp3Probe computes, so the engine
// never downloads or decodes what lies between. Position is the time the
// segment started at plus what it has played.
public sealed class BuiltinAudioPlayer : IAudioPlayer
{
    // what a forward jump may cost before a new segment is cheaper
    static readonly TimeSpan DecodeOverLimit = TimeSpan.FromSeconds(30);

    public string Name => "builtin";

    public PlayerCapabilities Capabilities { get; } =
        PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
        PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
        PlayerCapabilities.Network | PlayerCapabilities.Local;

    public PlayerState State { get; } = new();
    public event Action<PlayerState>? StateChanged;

    readonly object _gate = new();
    readonly SfEngine _engine;
    readonly AudioPlaybackDevice _device;
    readonly AudioFormat _format = new()
    {
        Format = SampleFormat.F32, SampleRate = 48000, Channels = 2,
        Layout = AudioFormat.GetLayoutFromChannels(2),
    };
    readonly HttpClient _http;
    readonly Timer _tick;

    Stream? _source;
    Mp3Info? _info;
    SoundPlayer? _player;
    Mp3SegmentProvider? _provider;
    TimeSpan _segmentStart;
    int _generation;
    bool _disposed;

    public BuiltinAudioPlayer() : this(silent: false) { }

    // silent: miniaudio's null backend, which plays in real time into
    // nothing; tests and machines without a sound card.
    internal BuiltinAudioPlayer(bool silent, HttpClient? http = null)
    {
        _http = http ?? new HttpClient(Podliner.Infra.Http.DualStackConnect.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
        // SoundFlow 1.4.1 hands its backend enum to miniaudio unchanged,
        // but numbers it from 1 where miniaudio starts at 0; 14 is
        // miniaudio's ma_backend_null (SoundFlow's own Null misses it).
        _engine = silent
            ? new MiniAudioEngine(new[] { (MiniAudioBackend)14 })
            : new MiniAudioEngine();
        try
        {
            _device = _engine.InitializePlaybackDevice(null, _format);
            _device.Start();
        }
        catch
        {
            _engine.Dispose();
            throw;
        }

        State.Capabilities = Capabilities;
        State.Speed = 1.0;
        _tick = new Timer(_ => Tick(), null, 250, 250);
    }

    public void Play(string url, long? startMs = null)
    {
        lock (_gate)
        {
            int gen = ++_generation;
            TearDownLocked();

            _source = Open(url);
            _info = Mp3Probe.Read(_source)
                    ?? throw new NotSupportedException($"builtin engine plays mp3 only, not {url}");
            if (gen != _generation) return;

            State.Length = _info.Duration;
            State.IsPlaying = true;
            StartSegmentLocked(TimeSpan.FromMilliseconds(Math.Max(0, startMs ?? 0)));
            Log.Information("builtin/play url={Url} length={Len} bitrate={Br}k", url, _info.Duration, _info.BitrateKbps);
        }
        Raise();
    }

    public void TogglePause()
    {
        lock (_gate)
        {
            if (_player == null) return;
            if (State.IsPlaying) _player.Pause(); else _player.Play();
            State.IsPlaying = !State.IsPlaying;
        }
        Raise();
    }

    public void SeekRelative(TimeSpan delta) => SeekTo(State.Position + delta);

    public void SeekTo(TimeSpan position)
    {
        lock (_gate)
        {
            if (_info == null || _source == null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (State.Length is { } len && position > len) position = len;

            var now = PositionLocked();
            if (_player != null && position >= now && position - now <= DecodeOverLimit)
            {
                // cheap: the decoder reads on to it
                _player.Seek(position - _segmentStart);
            }
            else
            {
                StartSegmentLocked(position);
            }
            State.Position = position;
        }
        Raise();
    }

    public void SetVolume(int vol0to100)
    {
        lock (_gate)
        {
            State.Volume0_100 = Math.Clamp(vol0to100, 0, 100);
            if (_player != null) _player.Volume = State.Volume0_100 / 100f;
        }
        Raise();
    }

    public void SetSpeed(double speed)
    {
        lock (_gate)
        {
            State.Speed = Math.Clamp(speed, 0.5, 3.0);
            if (_player != null) _player.PlaybackSpeed = (float)State.Speed;
        }
        Raise();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            TearDownLocked();
            State.IsPlaying = false;
            State.Position = TimeSpan.Zero;
        }
        Raise();
    }

    Stream Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return new HttpRangeStream(_http, url);
        var path = uri is { IsFile: true } ? uri.LocalPath : url;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
    }

    void StartSegmentLocked(TimeSpan at)
    {
        DropSegmentLocked();

        var offset = at <= TimeSpan.Zero ? _info!.AudioStart : _info!.OffsetFor(at);
        _segmentStart = at <= TimeSpan.Zero ? TimeSpan.Zero : at;

        _provider = new Mp3SegmentProvider(_engine, _format, new SegmentStream(_source!, offset));
        var player = new SoundPlayer(_engine, _format, _provider)
        {
            Volume = State.Volume0_100 / 100f,
            PlaybackSpeed = (float)State.Speed,
        };
        int gen = _generation;
        player.PlaybackEnded += (_, _) => OnEnded(gen);
        _device.MasterMixer.AddComponent(player);
        if (State.IsPlaying) player.Play();
        _player = player;
        State.Position = _segmentStart;
    }

    void OnEnded(int gen)
    {
        lock (_gate)
        {
            if (gen != _generation) return;
            State.IsPlaying = false;
            if (State.Length is { } len) State.Position = len;
        }
        Raise();
    }

    TimeSpan PositionLocked()
        => _player == null ? State.Position : _segmentStart + TimeSpan.FromSeconds(_player.Time);

    void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _player == null || !State.IsPlaying) return;
            State.Position = PositionLocked();
        }
        Raise();
    }

    void DropSegmentLocked()
    {
        if (_player != null)
        {
            try { _player.Stop(); } catch { }
            try { _device.MasterMixer.RemoveComponent(_player); } catch { }
            try { _player.Dispose(); } catch { }
        }
        try { _provider?.Dispose(); } catch { }
        _player = null;
        _provider = null;
    }

    void TearDownLocked()
    {
        DropSegmentLocked();
        try { _source?.Dispose(); } catch { }
        _source = null;
        _info = null;
        State.Length = null;
    }

    void Raise()
    {
        try { StateChanged?.Invoke(State); }
        catch (Exception ex) { Log.Debug(ex, "builtin/state subscriber threw"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            TearDownLocked();
        }
        try { _tick.Dispose(); } catch { }
        try { _device.Stop(); } catch { }
        try { _device.Dispose(); } catch { }
        try { _engine.Dispose(); } catch { }
        _http.Dispose();
    }
}

// A view of the source from a byte on, so a decoder can start mid-file.
//
// While Probing is set it ends 256 KB in. miniaudio asks a new decoder for
// its length straight away, and without a Xing header that means reading to
// the end: over HTTP, the whole episode before the first sound (measured:
// 57 MB for a 128 kbit/s hour). The engine never uses that length, so the
// probe gets a short file, and the real one afterwards.
internal sealed class SegmentStream : Stream
{
    const long ProbeWindow = 256 * 1024;
    readonly Stream _src;
    readonly long _start;
    long _pos;

    public SegmentStream(Stream src, long start) { _src = src; _start = start; }

    public bool Probing { get; set; }

    public override long Length => Math.Max(0, _src.Length - _start);
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, Length); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long end = Probing ? Math.Min(Length, ProbeWindow) : Length;
        if (_pos >= end) return 0;
        count = (int)Math.Min(count, end - _pos);
        _src.Position = _start + _pos;

        // miniaudio takes a short read for the end of the file, so fill it
        int got = 0;
        while (got < count)
        {
            int n = _src.Read(buffer, offset + got, count - got);
            if (n <= 0) break;
            got += n;
        }
        _pos += got;
        return got;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => Length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Feeds one segment's decoded samples to a SoundPlayer. The length it
// reports is open ended: SoundPlayer clamps its position to it and the
// segment has no reliable end of its own; the stream running out ends it.
internal sealed class Mp3SegmentProvider : ISoundDataProvider
{
    readonly ISoundDecoder _decoder;

    public Mp3SegmentProvider(SfEngine engine, AudioFormat format, SegmentStream stream)
    {
        stream.Probing = true;
        try { _decoder = engine.CreateDecoder(stream, "mp3", format); }
        finally { stream.Probing = false; }
        SampleRate = format.SampleRate;
        _decoder.EndOfStreamReached += (s, e) => EndOfStreamReached?.Invoke(this, e);
    }

    public int Position { get; private set; }
    public int Length => int.MaxValue / 2;
    public bool CanSeek => true;
    public SampleFormat SampleFormat => SampleFormat.F32;
    public int SampleRate { get; }
    public bool IsDisposed { get; private set; }
    public SoundFormatInfo? FormatInfo => null;

    public event EventHandler<EventArgs>? EndOfStreamReached;
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;

    public int ReadBytes(Span<float> buffer)
    {
        if (IsDisposed) return 0;
        int n = _decoder.Decode(buffer);
        Position += n;
        PositionChanged?.Invoke(this, new PositionChangedEventArgs(Position));
        return n;
    }

    public void Seek(int sampleOffset)
    {
        if (IsDisposed) return;
        if (_decoder.Seek(sampleOffset)) Position = sampleOffset;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _decoder.Dispose();
    }
}
