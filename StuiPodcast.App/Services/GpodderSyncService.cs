using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;

namespace StuiPodcast.App.Services;

// Orchestrates gPodder API v2 sync: login, pull/push subscriptions and episode actions.
// Entirely opt-in — if no credentials are configured, nothing runs.
sealed class GpodderSyncService : IDisposable
{
    private readonly GpodderStore        _store;
    private readonly IGpodderClient      _client;
    private readonly AppData             _data;
    private readonly PlaybackCoordinator _playback;
    private readonly Func<Task>?         _saveAsync;
    private readonly IKeyring            _keyring;

    // Episode action tracking state
    private int   _lastSessionId = -1;
    private Guid? _lastEpisodeId;

    public bool ShouldAutoSync => _store.Current.AutoSync && _store.Current.IsConfigured;

    public GpodderSyncService(
        GpodderStore        store,
        IGpodderClient      client,
        AppData             data,
        PlaybackCoordinator playback,
        Func<Task>?         saveAsync = null,
        IKeyring?           keyring   = null)
    {
        _store     = store;
        _client    = client;
        _data      = data;
        _playback  = playback;
        _saveAsync = saveAsync;
        _keyring   = keyring ?? new OsKeyring();

        // Pre-configure client from stored credentials so syncs work without explicit login.
        if (_store.Current.IsConfigured)
        {
            var pwd = ResolvePassword();
            if (pwd != null)
                _client.Configure(_store.Current.ServerUrl!, _store.Current.Username!, pwd);
        }

        _playback.SnapshotAvailable += OnSnapshot;
        _playback.StatusChanged     += OnStatusChanged;
    }

    // ── public API ───────────────────────────────────────────────────────────

    public async Task<(bool ok, string msg)> LoginAsync(string server, string username, string password)
    {
        if (!_data.NetworkOnline) return (false, "offline");

        try
        {
            var ok = await _client.LoginAsync(server, username, password);
            if (!ok) return (false, "login failed (check credentials)");

            _store.Current.ServerUrl = server.TrimEnd('/');
            _store.Current.Username  = username;

            // Try OS keyring first; fall back to plaintext in gpodder.json.
            bool keyringOk = _keyring.TrySet(username, password);
            if (keyringOk)
            {
                _store.Current.Password             = null;
                _store.Current.PasswordStoredInKeyring = true;
            }
            else
            {
                _store.Current.Password             = password;
                _store.Current.PasswordStoredInKeyring = false;
            }
            _store.Save();

            try { await _client.RegisterDeviceAsync(username, _store.Current.DeviceId); }
            catch (Exception ex) { Log.Warning(ex, "gpodder device registration failed"); }

            var resultMsg = $"logged in as {username} (device {_store.Current.DeviceId})";
            if (!keyringOk)
                resultMsg += "\nWarning: keyring unavailable – password stored in plaintext";

            return (true, resultMsg);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "gpodder login failed");
            return (false, $"login error: {ex.Message}");
        }
    }

    public void Logout()
    {
        // Remove keyring entry before wiping the username we look up by.
        if (_store.Current.PasswordStoredInKeyring && _store.Current.Username != null)
            _keyring.TryDelete(_store.Current.Username);

        _store.Current.ServerUrl               = null;
        _store.Current.Username                = null;
        _store.Current.Password                = null;
        _store.Current.PasswordStoredInKeyring = false;
        _store.Current.SubsTimestamp           = 0;
        _store.Current.ActionsTimestamp        = 0;
        _store.Current.LastKnownServerFeeds    = new();
        _store.Current.PendingActions          = new();
        _store.Current.LastSyncAt              = null;
        _store.Save();
    }

    // Pull then push.
    public async Task<(bool ok, string msg)> SyncAsync()
    {
        var (pullOk, pullMsg) = await PullAsync();
        var (pushOk, pushMsg) = await PushAsync();

        if (pullOk && pushOk)
            return (true, "sync complete");

        var parts = new List<string>();
        if (!pullOk) parts.Add($"pull: {pullMsg}");
        if (!pushOk) parts.Add($"push: {pushMsg}");
        return (false, string.Join("; ", parts));
    }

    // Pull subscription delta and episode actions from server.
    public async Task<(bool ok, string msg)> PullAsync()
    {
        if (!_data.NetworkOnline)            return (false, "offline");
        if (!_store.Current.IsConfigured)    return (false, "not configured");

        EnsureClientConfigured();

        try
        {
            var cfg = _store.Current;

            var delta = await _client.GetSubscriptionDeltaAsync(
                cfg.Username!, cfg.DeviceId, cfg.SubsTimestamp);

            int added = 0, removed = 0;

            foreach (var url in delta.Add)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (_data.Feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Best-effort placeholder; user can refresh later to fetch episodes.
                _data.Feeds.Add(new Feed { Title = url, Url = url });
                added++;
            }

            foreach (var url in delta.Remove)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                var feed = _data.Feeds.FirstOrDefault(f =>
                    string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
                if (feed == null) continue;
                _data.Feeds.Remove(feed);
                removed++;
            }

            cfg.SubsTimestamp        = delta.Timestamp;
            cfg.LastKnownServerFeeds = _data.Feeds
                .Where(f => !string.IsNullOrEmpty(f.Url))
                .Select(f => f.Url)
                .ToList();
            cfg.LastSyncAt = DateTimeOffset.UtcNow;
            _store.Save();

            if (_saveAsync != null && (added > 0 || removed > 0))
                _ = _saveAsync();

            return (true, $"pulled: +{added} -{removed} feeds");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "gpodder pull failed");
            return (false, $"pull error: {ex.Message}");
        }
    }

    // Push subscription changes and pending episode actions to server.
    public async Task<(bool ok, string msg)> PushAsync()
    {
        if (!_data.NetworkOnline)            return (false, "offline");
        if (!_store.Current.IsConfigured)    return (false, "not configured");

        EnsureClientConfigured();

        try
        {
            var cfg = _store.Current;

            // Subscription diff
            var currentUrls = _data.Feeds
                .Where(f => !string.IsNullOrEmpty(f.Url))
                .Select(f => f.Url)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var serverUrls = cfg.LastKnownServerFeeds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var add    = currentUrls.Except(serverUrls).ToArray();
            var remove = serverUrls.Except(currentUrls).ToArray();

            if (add.Length > 0 || remove.Length > 0)
            {
                var newTs = await _client.PushSubscriptionChangesAsync(
                    cfg.Username!, cfg.DeviceId, add, remove);
                cfg.SubsTimestamp        = newTs;
                cfg.LastKnownServerFeeds = currentUrls.ToList();
            }

            // Episode actions
            int actionCount = 0;
            if (cfg.PendingActions.Count > 0)
            {
                var actions = cfg.PendingActions.ToList();
                var newTs   = await _client.PushEpisodeActionsAsync(cfg.Username!, actions);
                cfg.ActionsTimestamp = newTs;
                cfg.PendingActions.Clear();
                actionCount = actions.Count;
            }

            cfg.LastSyncAt = DateTimeOffset.UtcNow;
            _store.Save();

            return (true, $"pushed: +{add.Length} -{remove.Length} subs, {actionCount} actions");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "gpodder push failed");
            return (false, $"push error: {ex.Message}");
        }
    }

    public void SetDeviceId(string id)
    {
        _store.Current.DeviceId = id.Length > 64 ? id[..64] : id;
        _store.Save();
    }

    public void SetAutoSync(bool? value)
    {
        _store.Current.AutoSync = value ?? !_store.Current.AutoSync;
        _store.Save();
    }

    public string GetStatus()
    {
        var cfg = _store.Current;
        if (!cfg.IsConfigured) return "sync: not configured";

        var lastSync = cfg.LastSyncAt.HasValue
            ? cfg.LastSyncAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "never";

        var pwdStore = cfg.PasswordStoredInKeyring ? "keyring" : "plaintext";

        return $"sync: {cfg.Username}@{cfg.ServerUrl}\n" +
               $"device: {cfg.DeviceId}  auto: {(cfg.AutoSync ? "on" : "off")}  pwd: {pwdStore}\n" +
               $"last sync: {lastSync}  pending actions: {cfg.PendingActions.Count}";
    }

    public void Dispose()
    {
        _playback.SnapshotAvailable -= OnSnapshot;
        _playback.StatusChanged     -= OnStatusChanged;
        _client.Dispose();
    }

    // ── episode action tracking ──────────────────────────────────────────────

    private void OnSnapshot(PlaybackSnapshot snap)
    {
        if (snap.SessionId == _lastSessionId) return;
        if (_lastEpisodeId.HasValue) QueuePlayAction(_lastEpisodeId.Value);
        _lastSessionId = snap.SessionId;
        _lastEpisodeId = snap.EpisodeId;
    }

    private void OnStatusChanged(PlaybackStatus status)
    {
        if (status == PlaybackStatus.Ended && _lastEpisodeId.HasValue)
            QueuePlayAction(_lastEpisodeId.Value);
    }

    private void QueuePlayAction(Guid episodeId)
    {
        var ep   = _data.Episodes.FirstOrDefault(e => e.Id == episodeId);
        var feed = ep != null ? _data.Feeds.FirstOrDefault(f => f.Id == ep.FeedId) : null;
        if (ep == null || feed == null || string.IsNullOrEmpty(feed.Url)) return;

        _store.Current.PendingActions.Add(new PendingGpodderAction
        {
            PodcastUrl = feed.Url,
            EpisodeUrl = ep.AudioUrl,
            Action     = "play",
            Timestamp  = DateTimeOffset.UtcNow,
            Position   = (int)(ep.Progress.LastPosMs / 1000),
            Total      = ep.DurationMs > 0 ? (int)(ep.DurationMs / 1000) : null,
        });
        _store.Save();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Returns the plaintext password by consulting keyring or the JSON fallback field.
    private string? ResolvePassword()
    {
        var cfg = _store.Current;
        if (!cfg.IsConfigured) return null;
        return cfg.PasswordStoredInKeyring
            ? _keyring.TryGet(cfg.Username!)
            : cfg.Password;
    }

    private void EnsureClientConfigured()
    {
        var cfg = _store.Current;
        if (!cfg.IsConfigured) return;
        var pwd = ResolvePassword();
        if (pwd != null)
            _client.Configure(cfg.ServerUrl!, cfg.Username!, pwd);
    }
}
