using System.Net;
using System.Net.Http.Headers;
using Serilog;

namespace StuiPodcast.Infra.Feeds;

// Owns the HttpClient used for feed XML fetches. Extracted from FeedService
// so the client lifecycle (headers, decompression, timeout) lives apart
// from the parsing/persistence logic. Test harnesses inject a
// HttpMessageHandler through the secondary constructor; production uses
// the default handler with GZip/Deflate/Brotli decompression.
//
// Podcast publishers often put CDN anti-bot filters in front of their
// feeds; the honest "podliner/1.0.1" User-Agent gets through most of them
// — audio streams that need a browser UA are handled separately by
// PlayerHttpDefaults on the audio engine side.
internal sealed class FeedHttpFetcher : IDisposable
{
    private readonly HttpClient _http;

    public FeedHttpFetcher() : this(BuildDefaultHandler()) { }

    public FeedHttpFetcher(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("podliner/1.0.1 (+https://github.com/yourrepo)");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
    }

    public async Task<string?> FetchXmlAsync(string url)
    {
        var r = await FetchAsync(url, null, null).ConfigureAwait(false);
        return r.Xml;
    }

    // Conditional-GET variant. Caller supplies the last-seen ETag +
    // Last-Modified; on 304 we return Xml=null + NotModified=true so the
    // service can skip the parse + persistence entirely. Freshness is only
    // updated on a real 200 body.
    public async Task<FetchResult> FetchAsync(string url, string? ifNoneMatch, string? ifModifiedSinceRaw)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrWhiteSpace(ifNoneMatch))
            {
                try { req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch); }
                catch (Exception ex) { Log.Debug(ex, "feed/http bad If-None-Match {Val}", ifNoneMatch); }
            }
            if (!string.IsNullOrWhiteSpace(ifModifiedSinceRaw))
            {
                try { req.Headers.TryAddWithoutValidation("If-Modified-Since", ifModifiedSinceRaw); }
                catch (Exception ex) { Log.Debug(ex, "feed/http bad If-Modified-Since {Val}", ifModifiedSinceRaw); }
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if ((int)resp.StatusCode == 304)
            {
                // Server says "nothing changed" — no body expected.
                return FetchResult.NotModified();
            }

            if (!resp.IsSuccessStatusCode)
            {
                Log.Warning("feed/http status={Status} url={Url}", (int)resp.StatusCode, url);
                return FetchResult.FailHttp((int)resp.StatusCode, resp.ReasonPhrase);
            }

            var xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            var etag = resp.Headers.ETag?.Tag; // includes surrounding quotes
            string? lastMod = null;
            if (resp.Content.Headers.LastModified is { } lm)
                lastMod = lm.ToUniversalTime().ToString("r"); // RFC1123

            return FetchResult.Ok(xml, etag, lastMod);
        }
        catch (TaskCanceledException ex)
        {
            Log.Warning(ex, "feed/http timeout url={Url}", url);
            return FetchResult.FailTimeout();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "feed/http unreachable url={Url}", url);
            return FetchResult.FailUnreachable(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "feed/http fail url={Url}", url);
            return FetchResult.FailOther(ex.Message);
        }
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { /* best effort */ }
    }

    private static HttpClientHandler BuildDefaultHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    };
}

// Result of a conditional-GET fetch. NotModified is the common hot path
// once conditional headers are cached: no body, no parse, no mutation.
// On failure the Category + FailureDetail let the caller build a
// user-friendly message instead of a generic "no content".
internal readonly record struct FetchResult(
    string? Xml,
    string? Etag,
    string? LastModified,
    bool IsNotModified,
    FetchFailure Failure,
    int? HttpStatus,
    string? FailureDetail)
{
    public bool IsOk => Xml != null;
    public bool IsFailure => !IsOk && !IsNotModified;

    public static FetchResult Ok(string xml, string? etag, string? lastModified)
        => new(xml, etag, lastModified, false, FetchFailure.None, null, null);
    public static FetchResult NotModified()
        => new(null, null, null, true, FetchFailure.None, null, null);
    public static FetchResult FailHttp(int status, string? reason)
        => new(null, null, null, false,
               status == 403 || status == 401 ? FetchFailure.Forbidden :
               status == 404                  ? FetchFailure.NotFound  :
               status >= 500                  ? FetchFailure.ServerError : FetchFailure.ClientError,
               status, reason);
    public static FetchResult FailTimeout()
        => new(null, null, null, false, FetchFailure.Timeout, null, "timed out");
    public static FetchResult FailUnreachable(string? detail)
        => new(null, null, null, false, FetchFailure.Unreachable, null, detail);
    public static FetchResult FailOther(string? detail)
        => new(null, null, null, false, FetchFailure.Other, null, detail);
}

internal enum FetchFailure
{
    None,
    NotFound,      // 404
    Forbidden,     // 403/401
    ServerError,   // 5xx
    ClientError,   // other 4xx
    Timeout,
    Unreachable,   // DNS/TCP/TLS failure
    Other
}
