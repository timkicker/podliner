using System.Text;
using System.Text.Json;
using Serilog;
using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

// Nextcloud gpoddersync flavor (github.com/thrillfall/nextcloud-gpodder).
// Mounted under /index.php/apps/gpoddersync/; stateless Basic-Auth per
// request; no /login, /logout or /devices endpoints (the Nextcloud user
// is resolved server-side from the framework). Username/deviceId
// parameters on the IGpodderClient surface are ignored for path-building —
// kept only for interface compatibility with the gpodder.net flavor.
public sealed class NextcloudGpodderClient : GpodderClientBase
{
    public NextcloudGpodderClient() : base() { }
    public NextcloudGpodderClient(HttpMessageHandler handler) : base(handler) { }

    public override void Configure(string server, string username, string password)
    {
        // Strip trailing slash and, if present, any trailing /index.php so
        // the app-path stays consistent no matter which form the user
        // pasted into :sync login.
        var s = server.TrimEnd('/');
        if (s.EndsWith("/index.php", StringComparison.OrdinalIgnoreCase))
            s = s[..^"/index.php".Length];
        Server   = s;
        Username = username;
        Password = password;
    }

    // Nextcloud has no dedicated login endpoint. We probe the
    // subscriptions list (cheapest authenticated GET) and treat 2xx as ok.
    public override async Task<bool> LoginAsync(string server, string username, string password)
    {
        Configure(server, username, password);

        var url = $"{Server}/index.php/apps/gpoddersync/subscriptions?since=0";
        var req = CreateRequest(HttpMethod.Get, url);

        Log.Information("nextcloud/login-probe GET {Url} user={User}", url, username);
        var resp = await Http.SendAsync(req);
        LastLoginStatus = (int)resp.StatusCode;
        LastLoginReason = resp.ReasonPhrase;
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warning("nextcloud/login-probe failed status={Status} reason={Reason} url={Url}",
                (int)resp.StatusCode, resp.ReasonPhrase, url);
            return false;
        }
        return true;
    }

    // No-op: Nextcloud gpoddersync stores data per Nextcloud user, no
    // device concept. Kept for interface compatibility.
    public override Task RegisterDeviceAsync(string username, string deviceId) => Task.CompletedTask;

    // GET /index.php/apps/gpoddersync/subscriptions?since={ts}
    public override async Task<SubscriptionDelta> GetSubscriptionDeltaAsync(string username, string deviceId, long since)
    {
        var url = $"{Server}/index.php/apps/gpoddersync/subscriptions?since={since}";
        var req = CreateRequest(HttpMethod.Get, url);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<SubscriptionDeltaDto>(stream, ReadOptions)
                  ?? new SubscriptionDeltaDto();
        return new SubscriptionDelta(dto.Add ?? [], dto.Remove ?? [], dto.Timestamp);
    }

    // POST /index.php/apps/gpoddersync/subscription_change/create
    public override async Task<long> PushSubscriptionChangesAsync(string username, string deviceId, string[] add, string[] remove)
    {
        var url  = $"{Server}/index.php/apps/gpoddersync/subscription_change/create";
        var body = JsonSerializer.Serialize(new { add, remove });
        var req  = CreateRequest(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<TimestampDto>(stream, ReadOptions);
        return dto?.Timestamp ?? 0;
    }

    // GET /index.php/apps/gpoddersync/episode_action?since={ts}
    public override async Task<EpisodeActionsResult> GetEpisodeActionsAsync(string username, long since)
    {
        var url = $"{Server}/index.php/apps/gpoddersync/episode_action?since={since}";
        var req = CreateRequest(HttpMethod.Get, url);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        var dto = await JsonSerializer.DeserializeAsync<EpisodeActionsResponseDto>(stream, ReadOptions)
                  ?? new EpisodeActionsResponseDto();

        var actions = (dto.Actions ?? []).Select(MapAction).ToArray();
        return new EpisodeActionsResult(actions, dto.Timestamp);
    }

    // POST /index.php/apps/gpoddersync/episode_action/create  (body = bare JSON array)
    // Server silently filters non-"play" actions; we only emit play anyway.
    public override async Task<long> PushEpisodeActionsAsync(string username, IEnumerable<PendingGpodderAction> actions)
    {
        var url  = $"{Server}/index.php/apps/gpoddersync/episode_action/create";
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
