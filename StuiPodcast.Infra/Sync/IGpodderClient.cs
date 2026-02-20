using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

public interface IGpodderClient : IDisposable
{
    void Configure(string server, string username, string password);
    Task<bool> LoginAsync(string server, string username, string password);
    Task RegisterDeviceAsync(string username, string deviceId);
    Task<SubscriptionDelta> GetSubscriptionDeltaAsync(string username, string deviceId, long since);
    Task<long> PushSubscriptionChangesAsync(string username, string deviceId, string[] add, string[] remove);
    Task<EpisodeActionsResult> GetEpisodeActionsAsync(string username, long since);
    Task<long> PushEpisodeActionsAsync(string username, IEnumerable<PendingGpodderAction> actions);
}
