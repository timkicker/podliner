using System.Net;
using System.Net.Http.Headers;
using Serilog;

namespace Podliner.Infra.Player;

// A seekable, read-only stream over an HTTP URL for the built-in engine.
//
// Reads go through one open response for as long as they run on; a jump
// opens a new request with a Range header at the new byte. Short steps
// forward are read over instead, which is cheaper than a new request. A
// server that ignores Range still works: the bytes before the target are
// read and dropped.
//
// The last megabyte read stays in memory. miniaudio reads a little, goes
// back to the start and reads again, four to six times while a decoder
// starts, and without the window each of those was a new request.
internal sealed class HttpRangeStream : Stream
{
    const long ReadOverLimit = 256 * 1024;
    const int WindowSize = 1024 * 1024;

    readonly HttpClient _http;
    readonly string _origin;    // the enclosure url from the feed
    string _url;                // where its redirects ended, once known
    readonly bool _rangesHonoured;

    HttpResponseMessage? _response;
    Stream? _body;
    long _bodyPos;      // offset the open body will deliver next
    long _pos;

    // the bytes just before _bodyPos: _window[0.._winLen] is _bodyPos - _winLen onwards
    readonly byte[] _window = new byte[WindowSize];
    int _winLen;

    public HttpRangeStream(HttpClient http, string url)
    {
        _http = http;
        _origin = url;
        _url = url;

        // The first request asks for everything from 0 as a range; the
        // answer says both how long the file is and whether ranges work.
        Open(0, out var total, out _rangesHonoured);
        if (total <= 0)
        {
            CloseBody();
            throw new IOException($"no length for {_url}, cannot seek in it");
        }
        Length = total;
    }

    public override long Length { get; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, Length);
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

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= Length || buffer.IsEmpty) return 0;

        long winStart = _bodyPos - _winLen;
        if (_body != null && _pos >= winStart && _pos < _bodyPos)
        {
            int n = (int)Math.Min(buffer.Length, _bodyPos - _pos);
            _window.AsSpan((int)(_pos - winStart), n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Reach(_pos);
                int n = _body!.Read(buffer);
                if (n == 0 && _pos < Length) throw new IOException("connection closed early");
                Remember(buffer[..n]);
                _pos += n;
                return n;
            }
            catch (Exception ex) when (attempt == 0 && ex is IOException or HttpRequestException)
            {
                Log.Debug(ex, "range-stream read failed at {Pos}, reopening", _pos);
                CloseBody();
            }
        }
    }

    // Make the open body deliver `target` next.
    void Reach(long target)
    {
        if (_body != null && target >= _bodyPos && target - _bodyPos <= ReadOverLimit)
        {
            Skip(target - _bodyPos);
            return;
        }

        CloseBody();
        if (_rangesHonoured)
        {
            Open(target, out _, out _);
        }
        else
        {
            Open(0, out _, out _);
            Skip(target);
        }
    }

    void Skip(long n)
    {
        var scratch = new byte[Math.Min(n, 64 * 1024)];
        while (n > 0)
        {
            int got = _body!.Read(scratch, 0, (int)Math.Min(n, scratch.Length));
            if (got <= 0) throw new IOException("connection closed early");
            n -= got;
            Remember(scratch.AsSpan(0, got));
        }
    }

    // Append what the body just delivered to the window, dropping its
    // oldest bytes when it is full.
    void Remember(ReadOnlySpan<byte> got)
    {
        if (got.Length >= WindowSize)
        {
            got[^WindowSize..].CopyTo(_window);
            _winLen = WindowSize;
        }
        else
        {
            int drop = Math.Max(0, _winLen + got.Length - WindowSize);
            if (drop > 0)
            {
                Buffer.BlockCopy(_window, drop, _window, 0, _winLen - drop);
                _winLen -= drop;
            }
            got.CopyTo(_window.AsSpan(_winLen));
            _winLen += got.Length;
        }
        _bodyPos += got.Length;
    }

    void Open(long from, out long total, out bool ranged)
    {
        var resp = Send(from);

        // A signed CDN link can expire while an episode sits paused; the
        // feed url redirects to a fresh one.
        if (!resp.IsSuccessStatusCode && _url != _origin)
        {
            Log.Debug("range-stream {Code} from {Url}, back through the feed url", (int)resp.StatusCode, _url);
            resp.Dispose();
            _url = _origin;
            resp = Send(from);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw new HttpRequestException($"HTTP {code} for {_url}", null, (HttpStatusCode)code);
        }

        // Enclosures run through tracker redirects, 2 to 5 of them on NPR,
        // Simplecast and Megaphone; later requests go straight to the end.
        if (resp.RequestMessage?.RequestUri is { } landed)
            _url = landed.ToString();

        ranged = resp.StatusCode == HttpStatusCode.PartialContent;
        total = ranged
            ? resp.Content.Headers.ContentRange?.Length ?? -1
            : resp.Content.Headers.ContentLength ?? -1;

        _response = resp;
        _body = resp.Content.ReadAsStream();
        _bodyPos = ranged ? from : 0;
        _winLen = 0;
    }

    HttpResponseMessage Send(long from)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.Range = new RangeHeaderValue(from, null);
        req.Headers.UserAgent.ParseAdd(PlayerHttpDefaults.UserAgent);
        Log.Debug("range-stream open from={From} url={Url}", from, _url);
        return _http.Send(req, HttpCompletionOption.ResponseHeadersRead);
    }

    void CloseBody()
    {
        try { _body?.Dispose(); } catch { }
        try { _response?.Dispose(); } catch { }
        _body = null;
        _response = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseBody();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
