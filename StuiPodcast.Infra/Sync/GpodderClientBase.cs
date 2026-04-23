using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

// Shared plumbing for every gPodder-compatible sync flavor:
//  - Owns the HttpClient and the CookieContainer (session fallback).
//  - Carries Basic-Auth credentials and appends them to each request.
//  - Centralises the JSON DTOs + options so the concrete clients only
//    have to describe their URL layout and a couple of edge cases.
// Flavor-specific behaviour (login semantics, URL construction, device
// concept) lives in the subclasses — everything here is shape-neutral.
public abstract class GpodderClientBase : IGpodderClient
{
    private readonly CookieContainer _cookies = new();
    protected readonly HttpClient Http;

    protected string? Server;
    protected string? Username;
    protected string? Password;

    // Diagnostic info from the most recent login attempt (for user-facing
    // error messages; null before login or on exception).
    public int?    LastLoginStatus { get; protected set; }
    public string? LastLoginReason { get; protected set; }

    protected GpodderClientBase()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    // For testing: inject a custom HttpMessageHandler. Skips the cookie
    // container plumbing since test handlers don't need sessions.
    protected GpodderClientBase(HttpMessageHandler handler)
    {
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public virtual void Configure(string server, string username, string password)
    {
        Server   = server.TrimEnd('/');
        Username = username;
        Password = password;
    }

    public abstract Task<bool> LoginAsync(string server, string username, string password);
    public abstract Task RegisterDeviceAsync(string username, string deviceId);
    public abstract Task<SubscriptionDelta> GetSubscriptionDeltaAsync(string username, string deviceId, long since);
    public abstract Task<long> PushSubscriptionChangesAsync(string username, string deviceId, string[] add, string[] remove);
    public abstract Task<EpisodeActionsResult> GetEpisodeActionsAsync(string username, long since);
    public abstract Task<long> PushEpisodeActionsAsync(string username, IEnumerable<PendingGpodderAction> actions);

    public void Dispose() => Http.Dispose();

    // ── shared helpers ──────────────────────────────────────────────────────

    protected HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        AddBasicAuth(req);
        return req;
    }

    protected void AddBasicAuth(HttpRequestMessage req)
    {
        if (Username == null || Password == null) return;
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")));
    }

    protected static PendingGpodderAction MapAction(EpisodeActionDto a) => new()
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

    protected static EpisodeActionDto MapToDto(PendingGpodderAction a) => new()
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

    protected static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    protected static readonly JsonSerializerOptions WriteNullOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── shared DTOs ─────────────────────────────────────────────────────────

    protected sealed class SubscriptionDeltaDto
    {
        [JsonPropertyName("add")]       public string[]? Add       { get; set; }
        [JsonPropertyName("remove")]    public string[]? Remove    { get; set; }
        [JsonPropertyName("timestamp")] public long      Timestamp { get; set; }
    }

    protected sealed class TimestampDto
    {
        [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    }

    protected sealed class EpisodeActionsResponseDto
    {
        [JsonPropertyName("actions")]   public EpisodeActionDto[]? Actions   { get; set; }
        [JsonPropertyName("timestamp")] public long                Timestamp { get; set; }
    }

    protected sealed class EpisodeActionDto
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
