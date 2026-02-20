using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

// Pure HTTP client for the gPodder API v2.
// Owns HttpClient + CookieContainer; session cookie is reused automatically.
// Always includes Basic Auth header as a reliable fallback.
public sealed class GpodderClient : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    private string? _server;
    private string? _username;
    private string? _password;

    public GpodderClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    // Configure credentials from stored config (no HTTP round-trip).
    public void Configure(string server, string username, string password)
    {
        _server   = server.TrimEnd('/');
        _username = username;
        _password = password;
    }

    // POST /api/2/auth/{username}/login.json  (Basic Auth)
    public async Task<bool> LoginAsync(string server, string username, string password)
    {
        Configure(server, username, password);

        var url = $"{_server}/api/2/auth/{Uri.EscapeDataString(username)}/login.json";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddBasicAuth(req);

        var resp = await _http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    // PUT /api/2/devices/{username}/{deviceId}.json
    public async Task RegisterDeviceAsync(string username, string deviceId)
    {
        var url = $"{_server}/api/2/devices/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(deviceId)}.json";
        var req = CreateRequest(HttpMethod.Put, url);
        req.Content = new StringContent(
            """{"caption":"Podliner","type":"desktop"}""",
            Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    // GET /api/2/subscriptions/{username}/{deviceId}.json?since={ts}
    public async Task<SubscriptionDelta> GetSubscriptionDeltaAsync(string username, string deviceId, long since)
    {
        var url = $"{_server}/api/2/subscriptions/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(deviceId)}.json?since={since}";
        var req = CreateRequest(HttpMethod.Get, url);
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<SubscriptionDeltaDto>(stream, ReadOptions)
                  ?? new SubscriptionDeltaDto();
        return new SubscriptionDelta(dto.Add ?? [], dto.Remove ?? [], dto.Timestamp);
    }

    // POST /api/2/subscriptions/{username}/{deviceId}.json  → returns new timestamp
    public async Task<long> PushSubscriptionChangesAsync(string username, string deviceId, string[] add, string[] remove)
    {
        var url  = $"{_server}/api/2/subscriptions/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(deviceId)}.json";
        var body = JsonSerializer.Serialize(new { add, remove });
        var req  = CreateRequest(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<TimestampDto>(stream, ReadOptions);
        return dto?.Timestamp ?? 0;
    }

    // GET /api/2/episodes/{username}.json?since={ts}
    public async Task<EpisodeActionsResult> GetEpisodeActionsAsync(string username, long since)
    {
        var url = $"{_server}/api/2/episodes/{Uri.EscapeDataString(username)}.json?since={since}";
        var req = CreateRequest(HttpMethod.Get, url);
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<EpisodeActionsResponseDto>(stream, ReadOptions)
                  ?? new EpisodeActionsResponseDto();

        var actions = (dto.Actions ?? []).Select(MapAction).ToArray();
        return new EpisodeActionsResult(actions, dto.Timestamp);
    }

    // POST /api/2/episodes/{username}.json  → returns new timestamp
    public async Task<long> PushEpisodeActionsAsync(string username, IEnumerable<PendingGpodderAction> actions)
    {
        var url  = $"{_server}/api/2/episodes/{Uri.EscapeDataString(username)}.json";
        var dtos = actions.Select(MapToDto);
        var body = JsonSerializer.Serialize(dtos, WriteNullOptions);
        var req  = CreateRequest(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<TimestampDto>(stream, ReadOptions);
        return dto?.Timestamp ?? 0;
    }

    public void Dispose() => _http.Dispose();

    // ── helpers ─────────────────────────────────────────────────────────────

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        AddBasicAuth(req);
        return req;
    }

    private void AddBasicAuth(HttpRequestMessage req)
    {
        if (_username == null || _password == null) return;
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}")));
    }

    private static PendingGpodderAction MapAction(EpisodeActionDto a) => new()
    {
        PodcastUrl = a.Podcast ?? "",
        EpisodeUrl = a.Episode ?? "",
        Action     = a.Action  ?? "play",
        Timestamp  = DateTimeOffset.TryParse(a.Timestamp, out var ts) ? ts : DateTimeOffset.UtcNow,
        Started    = a.Started,
        Position   = a.Position,
        Total      = a.Total,
        Guid       = a.Guid
    };

    private static EpisodeActionDto MapToDto(PendingGpodderAction a) => new()
    {
        Podcast   = a.PodcastUrl,
        Episode   = a.EpisodeUrl,
        Action    = a.Action,
        Timestamp = a.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
        Started   = a.Started,
        Position  = a.Position,
        Total     = a.Total,
        Guid      = a.Guid
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteNullOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── internal DTOs ────────────────────────────────────────────────────────

    private sealed class SubscriptionDeltaDto
    {
        [JsonPropertyName("add")]       public string[]? Add       { get; set; }
        [JsonPropertyName("remove")]    public string[]? Remove    { get; set; }
        [JsonPropertyName("timestamp")] public long      Timestamp { get; set; }
    }

    private sealed class TimestampDto
    {
        [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    }

    private sealed class EpisodeActionsResponseDto
    {
        [JsonPropertyName("actions")]   public EpisodeActionDto[]? Actions   { get; set; }
        [JsonPropertyName("timestamp")] public long                Timestamp { get; set; }
    }

    private sealed class EpisodeActionDto
    {
        [JsonPropertyName("podcast")]   public string? Podcast   { get; set; }
        [JsonPropertyName("episode")]   public string? Episode   { get; set; }
        [JsonPropertyName("action")]    public string? Action    { get; set; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
        [JsonPropertyName("started")]   public int?    Started   { get; set; }
        [JsonPropertyName("position")]  public int?    Position  { get; set; }
        [JsonPropertyName("total")]     public int?    Total     { get; set; }
        [JsonPropertyName("guid")]      public string? Guid      { get; set; }
    }
}

// ── public return types ───────────────────────────────────────────────────────

public record SubscriptionDelta(string[] Add, string[] Remove, long Timestamp);
public record EpisodeActionsResult(PendingGpodderAction[] Actions, long Timestamp);
