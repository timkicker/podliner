using System.Net;
using System.Net.Http.Headers;

namespace Podliner.Infra.Tests.Fakes;

// Serves a byte array over a fake HTTP handler, honouring Range (or not,
// like some servers), and counts the bytes the client actually read.
sealed class RangeFileServer : HttpMessageHandler
{
    readonly byte[] _data;
    readonly bool _ranges;
    long _bytesRead;

    public readonly List<(long? From, string? UserAgent)> Requests = new();
    public readonly List<string> Urls = new();

    // Where requests for anything else end up, as if a tracker redirected
    // them; null serves every url directly.
    public string? RedirectTo { get; init; }

    // Set to make RedirectTo answer 403, like a signed CDN link that expired.
    public bool RedirectTargetExpired { get; set; }
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public RangeFileServer(byte[] data, bool ranges = true) { _data = data; _ranges = ranges; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => Task.FromResult(Send(req, ct));

    protected override HttpResponseMessage Send(HttpRequestMessage req, CancellationToken ct)
    {
        var from = req.Headers.Range?.Ranges.First().From;
        lock (Requests)
        {
            Requests.Add((from, req.Headers.UserAgent.ToString()));
            Urls.Add(req.RequestUri!.ToString());
        }
        if (RedirectTargetExpired && req.RequestUri!.ToString() == RedirectTo)
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = req };
        var resp = Answer(from);
        // HttpClientHandler reports the url it ended up at here
        resp.RequestMessage = new HttpRequestMessage(req.Method, RedirectTo ?? req.RequestUri!.ToString());
        return resp;
    }

    HttpResponseMessage Answer(long? from)
    {
        if (_ranges && from is long f)
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = Body((int)f) };
            r.Content.Headers.ContentRange = new ContentRangeHeaderValue(f, _data.Length - 1, _data.Length);
            return r;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = Body(0) };
    }

    HttpContent Body(int from)
    {
        var c = new StreamContent(new Counting(new MemoryStream(_data, from, _data.Length - from, writable: false), this));
        c.Headers.ContentLength = _data.Length - from;
        return c;
    }

    sealed class Counting : Stream
    {
        readonly Stream _inner;
        readonly RangeFileServer _owner;
        public Counting(Stream inner, RangeFileServer owner) { _inner = inner; _owner = owner; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            Interlocked.Add(ref _owner._bytesRead, n);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool d) { if (d) _inner.Dispose(); base.Dispose(d); }
    }
}
