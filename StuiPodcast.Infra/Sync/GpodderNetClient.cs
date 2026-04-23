using System.Text;
using System.Text.Json;
using Serilog;
using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

// gPodder API v2 flavor — gpodder.net and self-hosted gpodder-compatible
// services (mygpo, opodsync, etc.). Endpoints are under /api/2/ with
// explicit {username}/{deviceId} path segments and a dedicated login flow.
public class GpodderNetClient : GpodderClientBase
{
    public GpodderNetClient() : base() { }
    public GpodderNetClient(HttpMessageHandler handler) : base(handler) { }

    // POST /api/2/auth/{username}/login.json  (Basic Auth → session cookie)
    public override async Task<bool> LoginAsync(string server, string username, string password)
    {
        Configure(server, username, password);

        var url = $"{Server}/api/2/auth/{Uri.EscapeDataString(username)}/login.json";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddBasicAuth(req);

        Log.Information("gpodder.net/login POST {Url} user={User}", url, username);
        var resp = await Http.SendAsync(req);
        LastLoginStatus = (int)resp.StatusCode;
        LastLoginReason = resp.ReasonPhrase;
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warning("gpodder.net/login failed status={Status} reason={Reason} url={Url}",
                (int)resp.StatusCode, resp.ReasonPhrase, url);
            return false;
        }
        return true;
    }

    // PUT /api/2/devices/{username}/{deviceId}.json
    public override async Task RegisterDeviceAsync(string username, string deviceId)
    {
        var url = $"{Server}/api/2/devices/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(deviceId)}.json";
        var req = CreateRequest(HttpMethod.Put, url);
        req.Content = new StringContent(
            """{"caption":"Podliner","type":"desktop"}""",
            Encoding.UTF8, "application/json");
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    // GET /api/2/subscriptions/{username}/{deviceId}.json?since={ts}
    public override async Task<SubscriptionDelta> GetSubscriptionDeltaAsync(string username, string deviceId, long since)
    {
        var url = $"{Server}/api/2/subscriptions/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(deviceId)}.json?since={since}";
        var req = CreateRequest(HttpMethod.Get, url);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<SubscriptionDeltaDto>(stream, ReadOptions)
                  ?? new SubscriptionDeltaDto();
        return new SubscriptionDelta(dto.Add ?? [], dto.Remove ?? [], dto.Timestamp);
    }

    // POST /api/2/subscriptions/{username}/{deviceId}.json  → returns new timestamp
    public override async Task<long> PushSubscriptionChangesAsync(string username, string deviceId, string[] add, string[] remove)
    {
        var url  = $"{Server}/api/2/subscriptions/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(deviceId)}.json";
        var body = JsonSerializer.Serialize(new { add, remove });
        var req  = CreateRequest(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<TimestampDto>(stream, ReadOptions);
        return dto?.Timestamp ?? 0;
    }

    // GET /api/2/episodes/{username}.json?since={ts}
    public override async Task<EpisodeActionsResult> GetEpisodeActionsAsync(string username, long since)
    {
        var url = $"{Server}/api/2/episodes/{Uri.EscapeDataString(username)}.json?since={since}";
        var req = CreateRequest(HttpMethod.Get, url);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<EpisodeActionsResponseDto>(stream, ReadOptions)
                  ?? new EpisodeActionsResponseDto();

        var actions = (dto.Actions ?? []).Select(MapAction).ToArray();
        return new EpisodeActionsResult(actions, dto.Timestamp);
    }

    // POST /api/2/episodes/{username}.json  → returns new timestamp
    public override async Task<long> PushEpisodeActionsAsync(string username, IEnumerable<PendingGpodderAction> actions)
    {
        var url  = $"{Server}/api/2/episodes/{Uri.EscapeDataString(username)}.json";
        var dtos = actions.Select(MapToDto);
        var body = JsonSerializer.Serialize(dtos, WriteNullOptions);
        var req  = CreateRequest(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<TimestampDto>(stream, ReadOptions);
        return dto?.Timestamp ?? 0;
    }
}
