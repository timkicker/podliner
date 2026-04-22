using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.Infra.Sync;

// Persists gPodder sync state (server URL, username, device ID, timestamps,
// pending actions) to gpodder.json. Extends JsonStore<T>; the file is small
// so writes are nearly immediate (100 ms debounce).
//
// Exposes the historic synchronous `Save()` method so existing call sites
// keep working; internally that maps to SaveNow() on the base class.
public sealed class GpodderStore : JsonStore<GpodderSyncConfig>
{
    public GpodderStore(string configDir)
        : base(Path.Combine(configDir, "gpodder.json"), TimeSpan.FromMilliseconds(100))
    { }

    protected override GpodderSyncConfig CreateDefault()
        => new() { DeviceId = DefaultDeviceId() };

    protected override void ValidateAndNormalize(GpodderSyncConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DeviceId))
            cfg.DeviceId = DefaultDeviceId();
    }

    // Preserves the original method name used throughout GpodderSyncService.
    public void Save() => SaveNow();

    static string DefaultDeviceId()
    {
        var id = "podliner-" + Environment.MachineName.ToLowerInvariant();
        return id.Length > 64 ? id[..64] : id;
    }
}
