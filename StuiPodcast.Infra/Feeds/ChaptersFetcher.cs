using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Feeds;

// Fetches + parses a podcast:chapters JSON document. Lazy-loaded from
// ChaptersUrl when the user actually interacts with chapters on an episode
// (first :chapter command). Kept separate from FeedHttpFetcher so we don't
// share the tight RSS timeout, and so the JSON parse isn't entangled with
// XML feed parsing.
public sealed class ChaptersFetcher : IDisposable
{
    readonly HttpClient _http;

    public ChaptersFetcher() : this(BuildDefaultHandler()) { }

    public ChaptersFetcher(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("podliner/1.0.1 (+chapters)");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json+chapters"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
    }

    // Returns a parsed list (possibly empty) on success, or null if the
    // fetch/parse fails. Errors are logged but never thrown — chapters are
    // a nice-to-have, not a core feature.
    public async Task<List<Chapter>?> FetchAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Debug("chapters/http status={Status} url={Url}", (int)resp.StatusCode, url);
                return null;
            }
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "chapters/fetch failed url={Url}", url);
            return null;
        }
    }

    // ID3 CHAP fallback. Reads a local downloaded file and parses ID3v2
    // chapters from the tag region. Returns null if no chapters or parse
    // failure. Doesn't need network.
    public static List<Chapter>? FetchFromLocalFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Id3ChaptersParser.TryParse(fs);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "chapters/local-id3 failed path={Path}", path);
            return null;
        }
    }

    // ID3 CHAP fallback for streamed-only episodes. Done in two phases so
    // we don't waste bandwidth on podcasts with huge cover-art tags (we've
    // seen 6.6 MB tags dominated by APIC):
    //
    //   1. Fetch the first 10 bytes (ID3 header) to learn the exact tag size.
    //   2. Fetch exactly the tag region, capped at MaxTagBytes as a sanity
    //      limit (>16 MB ID3 tags aren't podcast chapters, they're spam).
    //
    // Falls back to silent failure on every error branch — chapters are
    // a nice-to-have, not mission-critical.
    const int MaxTagBytes = 16 * 1024 * 1024;

    public async Task<List<Chapter>?> FetchFromUrlId3Async(string audioUrl)
    {
        try
        {
            // Phase 1: the 10-byte ID3v2 header tells us the tag size.
            int tagTotal;
            using (var headReq = new HttpRequestMessage(HttpMethod.Get, audioUrl))
            {
                headReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
                using var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!headResp.IsSuccessStatusCode && (int)headResp.StatusCode != 206)
                {
                    Log.Debug("chapters/id3-header status={Status} url={Url}", (int)headResp.StatusCode, audioUrl);
                    return null;
                }
                var headBytes = await headResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (headBytes.Length < 10 || headBytes[0] != 'I' || headBytes[1] != 'D' || headBytes[2] != '3')
                    return null; // not an ID3-tagged MP3
                var synchsafe = ((headBytes[6] & 0x7F) << 21)
                              | ((headBytes[7] & 0x7F) << 14)
                              | ((headBytes[8] & 0x7F) << 7)
                              | (headBytes[9] & 0x7F);
                tagTotal = 10 + synchsafe;
                if (tagTotal <= 10 || tagTotal > MaxTagBytes)
                {
                    Log.Debug("chapters/id3-header implausible tagSize={Size} url={Url}", tagTotal, audioUrl);
                    return null;
                }
            }

            // Phase 2: pull the exact tag region.
            using var req = new HttpRequestMessage(HttpMethod.Get, audioUrl);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, tagTotal - 1);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 206)
            {
                Log.Debug("chapters/id3-range status={Status} url={Url}", (int)resp.StatusCode, audioUrl);
                return null;
            }

            // Range-support guard: if the server ignored our Range header and
            // returned 200 with the full body, Content-Length will be the
            // entire episode — we'd download 100+ MB just to read ID3. Bail
            // when the payload is materially larger than the announced tag
            // plus a small slack for edge-aligned responses.
            if ((int)resp.StatusCode == 200)
            {
                var declared = resp.Content.Headers.ContentLength ?? -1;
                if (declared > tagTotal + 65_536)
                {
                    Log.Debug("chapters/id3-range server ignored Range (status=200 len={Len} tagTotal={TagTotal}) url={Url}",
                        declared, tagTotal, audioUrl);
                    return null;
                }
            }

            await using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var ms = new MemoryStream(tagTotal);
            // Extra safety: cap the local copy so a misbehaving server that
            // doesn't honour Range AND hides Content-Length can't make us
            // eat unbounded memory.
            await CopyBoundedAsync(s, ms, tagTotal + 65_536).ConfigureAwait(false);
            ms.Position = 0;
            return Id3ChaptersParser.TryParse(ms);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "chapters/id3-range failed url={Url}", audioUrl);
            return null;
        }
    }

    // Public for unit tests. Accepts the Podcast-2.0 chapters JSON schema.
    public static List<Chapter>? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chapters", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<Chapter>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
            {
                // startTime is required by spec.
                if (!el.TryGetProperty("startTime", out var startEl)) continue;
                double start = startEl.ValueKind switch
                {
                    JsonValueKind.Number => startEl.GetDouble(),
                    JsonValueKind.String when double.TryParse(startEl.GetString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
                    _ => double.NaN
                };
                if (double.IsNaN(start) || start < 0) continue;

                var title = el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? ""
                    : "";

                var chapUrl = el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()
                    : null;
                var img = el.TryGetProperty("img", out var i) && i.ValueKind == JsonValueKind.String
                    ? i.GetString()
                    : null;

                list.Add(new Chapter { StartSeconds = start, Title = title, Url = chapUrl, Img = img });
            }

            // Spec: sorted by startTime ascending. We re-sort because some
            // publishers emit out-of-order (seen in the wild).
            list.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            return list;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "chapters/parse failed");
            return null;
        }
    }

    // Copies from source to dest up to `maxBytes`. Stops short if the
    // source is shorter. Stops exactly at the cap otherwise — any trailing
    // bytes are discarded, which is fine for our "read the ID3 tag region"
    // use case since everything past the announced tag size is audio data.
    static async Task CopyBoundedAsync(Stream src, Stream dst, int maxBytes)
    {
        var buf = new byte[Math.Min(81_920, maxBytes)];
        int total = 0;
        while (total < maxBytes)
        {
            int want = Math.Min(buf.Length, maxBytes - total);
            int read = await src.ReadAsync(buf.AsMemory(0, want)).ConfigureAwait(false);
            if (read <= 0) break;
            await dst.WriteAsync(buf.AsMemory(0, read)).ConfigureAwait(false);
            total += read;
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
