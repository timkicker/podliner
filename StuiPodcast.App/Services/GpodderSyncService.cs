using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;

namespace StuiPodcast.App.Services;

// Orchestrates gPodder API v2 sync: login, pull/push subscriptions and episode actions.
// Entirely opt-in — if no credentials are configured, nothing runs.
sealed class GpodderSyncService : IDisposable
{
    private readonly GpodderStore          _store;
    private readonly IGpodderClientFactory _factory;
    private IGpodderClient                 _client;
    private GpodderFlavor                  _flavor;
    private readonly AppData               _data;
    private readonly PlaybackCoordinator   _playback;
    private readonly Func<Task>?           _saveAsync;
    private readonly IKeyring              _keyring;
    // Marshals feed-store mutations to the UI thread to avoid races with UI reads.
    // Defaults to synchronous (test-friendly); Program.cs wires it to Application.MainLoop.Invoke.
    private readonly Func<Action, Task>    _uiDispatch;
    private readonly IEpisodeStore         _episodes;
    private readonly IFeedStore            _feedStore;

    public GpodderFlavor Flavor => _flavor;

    // Episode action tracking state
    private int   _lastSessionId = -1;
    private Guid? _lastEpisodeId;

    // Reentrance guard so a startup auto-sync and a user-triggered :sync
    // don't race on _store.Current (PendingActions clear + server push).
    private int _syncing;

    public bool ShouldAutoSync => _store.Current.AutoSync && _store.Current.IsConfigured;

    // Back-compat constructor: still accepts a single IGpodderClient. The
    // flavor stays pinned to whatever the caller injected — used by tests
    // and the pre-multi-flavor call sites.
    public GpodderSyncService(
        GpodderStore        store,
        IGpodderClient      client,
        AppData             data,
        PlaybackCoordinator playback,
        IEpisodeStore       episodes,
        IFeedStore          feedStore,
        Func<Task>?         saveAsync  = null,
        IKeyring?           keyring    = null,
        Func<Action, Task>? uiDispatch = null)
        : this(store, new FixedClientFactory(client), data, playback, episodes, feedStore,
               saveAsync, keyring, uiDispatch) { }

    public GpodderSyncService(
        GpodderStore          store,
        IGpodderClientFactory factory,
        AppData               data,
        PlaybackCoordinator   playback,
        IEpisodeStore         episodes,
        IFeedStore            feedStore,
        Func<Task>?           saveAsync  = null,
        IKeyring?             keyring    = null,
        Func<Action, Task>?   uiDispatch = null)
    {
        _store      = store;
        _factory    = factory;
        _data       = data;
        _playback   = playback;
        _saveAsync  = saveAsync;
        _keyring    = keyring    ?? new OsKeyring();
        _uiDispatch = uiDispatch ?? (a => { a(); return Task.CompletedTask; });
        _episodes   = episodes  ?? throw new ArgumentNullException(nameof(episodes));
        _feedStore  = feedStore ?? throw new ArgumentNullException(nameof(feedStore));

        // Honour the stored flavor (persists across restarts so we don't
        // re-probe on every launch). Pre-flavor configs hit the Auto branch
        // which defaults to the gpodder.net client — matches legacy behaviour.
        _flavor = GpodderFlavorExt.FromWire(_store.Current.Flavor);
        _client = _factory.Create(_flavor);

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

    // Trivial factory shim for call sites that already constructed a client.
    sealed class FixedClientFactory : IGpodderClientFactory
    {
        readonly IGpodderClient _fixed;
        public FixedClientFactory(IGpodderClient c) { _fixed = c; }
        public IGpodderClient Create(GpodderFlavor _) => _fixed;
    }

    // ── public API ───────────────────────────────────────────────────────────

    public async Task<(bool ok, string msg)> LoginAsync(string server, string username, string password)
    {
        if (!_data.NetworkOnline) return (false, "offline");

        try
        {
            Log.Information("gpodder/sync login attempt server={Server} user={User}", server, username);

            // Flavor detection — try the most likely protocol first based on
            // the URL, fall back to the other. Skip the probe entirely if a
            // flavor is already persisted (normal steady-state case).
            var (ok, chosen, attempts) = await DetectFlavorAndLoginAsync(server, username, password);
            if (!ok)
            {
                var status = _client.LastLoginStatus;
                var reason = _client.LastLoginReason;
                var detail = status.HasValue
                    ? $"HTTP {status}{(string.IsNullOrWhiteSpace(reason) ? "" : $" {reason}")}"
                    : "unknown error";
                Log.Warning("gpodder/sync login failed server={Server} detail={Detail} tried={Tried}",
                    server, detail, string.Join(",", attempts));

                var hint = "";
                if (status == 404 && attempts.Count == 1 && !attempts.Contains(GpodderFlavor.Nextcloud))
                {
                    // Only happens on an auth-abort path (401 on first flavor
                    // stops probing before Nextcloud is tried). Leave the old
                    // hint as-is for that narrow case.
                    hint = " (endpoint not found — Nextcloud gPodder-Sync uses a different API path)";
                }
                else if (status == 404)
                {
                    hint = " — neither gpodder.net nor Nextcloud endpoints responded; check the server URL";
                }
                else if (status == 401)
                    hint = " (wrong password? for 2FA Nextcloud accounts create an app-password in Settings → Security)";

                return (false, $"login failed: {detail}{hint}");
            }

            _flavor = chosen;
            _store.Current.Flavor = chosen.ToWire();

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

            var flavorLabel = _flavor == GpodderFlavor.Nextcloud ? "Nextcloud" : "gpodder.net";
            var resultMsg = $"logged in as {username} via {flavorLabel} (device {_store.Current.DeviceId})";
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

    // Try each flavor in priority order (URL-hinted flavor first) and stop
    // at the first success. Swaps `_client` atomically so the winning
    // flavor is the one used for subsequent operations. Returns the list
    // of flavors actually tried for diagnostic logging.
    async Task<(bool ok, GpodderFlavor chosen, List<GpodderFlavor> attempts)> DetectFlavorAndLoginAsync(
        string server, string username, string password)
    {
        var attempts = new List<GpodderFlavor>();
        var order = OrderFlavorsForUrl(server);

        foreach (var flavor in order)
        {
            attempts.Add(flavor);
            var candidate = _factory.Create(flavor);
            Log.Debug("gpodder/detect trying flavor={Flavor} server={Server}", flavor, server);
            bool ok;
            try
            {
                ok = await candidate.LoginAsync(server, username, password);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "gpodder/detect flavor={Flavor} threw", flavor);
                try { candidate.Dispose(); } catch { }
                continue;
            }

            if (ok)
            {
                // Swap the live client. Disposing the previous one is safe
                // because we only hold a reference locally past this point.
                var old = _client;
                _client = candidate;
                try { old.Dispose(); } catch { }
                return (true, flavor, attempts);
            }

            // 401 = creds bad, no point trying the other flavor (would
            // also 401). Report now with the actual server response.
            if (candidate.LastLoginStatus == 401)
            {
                // Surface via _client for the outer reason-formatter.
                try { _client.Dispose(); } catch { }
                _client = candidate;
                return (false, flavor, attempts);
            }

            try { candidate.Dispose(); } catch { }
        }

        // Neither flavor accepted; last candidate remains in _client for
        // error-message extraction. Rebuild a fresh one for the Auto state
        // so we don't leak diagnostics across attempts.
        return (false, GpodderFlavor.Auto, attempts);
    }

    // Cheap heuristic: a URL that already contains /index.php is almost
    // certainly Nextcloud; everything else we try gpodder.net first.
    static List<GpodderFlavor> OrderFlavorsForUrl(string server)
    {
        var isNextcloudHint = !string.IsNullOrEmpty(server) &&
                              (server.Contains("/index.php", StringComparison.OrdinalIgnoreCase) ||
                               server.Contains("gpoddersync", StringComparison.OrdinalIgnoreCase));
        return isNextcloudHint
            ? new List<GpodderFlavor> { GpodderFlavor.Nextcloud, GpodderFlavor.GpodderNet }
            : new List<GpodderFlavor> { GpodderFlavor.GpodderNet, GpodderFlavor.Nextcloud };
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
        _store.Current.Flavor                  = null;
        _flavor                                = GpodderFlavor.Auto;
        _store.Save();
    }

    // Pull then push.
    public async Task<(bool ok, string msg)> SyncAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _syncing, 1) == 1)
            return (false, "sync already in progress");

        try
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
        finally
        {
            System.Threading.Interlocked.Exchange(ref _syncing, 0);
        }
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
            List<string> snapshotUrls = new();

            // Mutate feeds on the UI thread so concurrent UI reads don't trip over us.
            await _uiDispatch(() =>
            {
                foreach (var url in delta.Add)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    if (_feedStore.ContainsUrl(url)) continue;

                    // Best-effort placeholder; user can refresh later to fetch episodes.
                    _feedStore.AddOrUpdate(new Feed { Title = url, Url = url });
                    added++;
                }

                foreach (var url in delta.Remove)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var feed = _feedStore.FindByUrl(url);
                    if (feed == null) continue;
                    _feedStore.Remove(feed.Id);
                    removed++;
                }

                snapshotUrls = _feedStore.Snapshot()
                    .Where(f => !string.IsNullOrEmpty(f.Url))
                    .Select(f => f.Url)
                    .ToList();
            }).ConfigureAwait(false);

            cfg.SubsTimestamp        = delta.Timestamp;
            cfg.LastKnownServerFeeds = snapshotUrls;
            cfg.LastSyncAt           = DateTimeOffset.UtcNow;
            _store.Save();

            if (_saveAsync != null && (added > 0 || removed > 0))
                _ = _saveAsync();

            return (true, $"pulled: +{added} -{removed} feeds");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "gpodder pull failed server={Server} user={User}",
                _store.Current.ServerUrl, _store.Current.Username);
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

            // Snapshot the feed URLs on the UI thread so we don't race a concurrent mutation.
            HashSet<string> currentUrls = new(StringComparer.OrdinalIgnoreCase);
            await _uiDispatch(() =>
            {
                foreach (var f in _feedStore.Snapshot())
                    if (!string.IsNullOrEmpty(f.Url)) currentUrls.Add(f.Url);
            }).ConfigureAwait(false);

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
            Log.Warning(ex, "gpodder push failed server={Server} user={User} pendingActions={Pending}",
                _store.Current.ServerUrl, _store.Current.Username, _store.Current.PendingActions?.Count ?? 0);
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
        var flavor   = _flavor == GpodderFlavor.Auto ? "unknown" : _flavor.ToWire();

        return $"sync: {cfg.Username}@{cfg.ServerUrl}  flavor: {flavor}\n" +
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
        var ep   = _episodes.Find(episodeId);
        var feed = ep == null ? null : _feedStore.Find(ep.FeedId);
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
        else
            Log.Warning("gpodder: could not resolve password for {User} (keyring unavailable?)", cfg.Username);
    }
}
