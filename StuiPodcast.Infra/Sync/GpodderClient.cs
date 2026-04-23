using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

// Backwards-compatible alias: callers that instantiated `GpodderClient`
// directly (pre-flavor-split) still get the gpodder.net behaviour they
// had. New code should construct via IGpodderClientFactory.
public sealed class GpodderClient : GpodderNetClient
{
    public GpodderClient() : base() { }
    public GpodderClient(HttpMessageHandler handler) : base(handler) { }
}

// ── public return types (shared across all gPodder flavors) ──────────────────

public record SubscriptionDelta(string[] Add, string[] Remove, long Timestamp);
public record EpisodeActionsResult(PendingGpodderAction[] Actions, long Timestamp);
