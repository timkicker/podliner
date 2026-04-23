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
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Log.Warning("feed/http status={Status} url={Url}", (int)resp.StatusCode, url);
                return null;
            }

            // Return the raw body as string; CodeHollow.FeedReader handles
            // the XML encoding declaration itself.
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "feed/http fail url={Url}", url);
            return null;
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
