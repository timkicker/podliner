using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;

namespace StuiPodcast.App.Tests.Fakes;

sealed class FakeGpodderClient : IGpodderClient
{
    public bool LoginResult   { get; set; } = true;
    public bool ThrowOnPush   { get; set; } = false;
    public long NextTimestamp { get; set; } = 100;
    public SubscriptionDelta? NextDelta { get; set; }

    public List<(string[] add, string[] remove)> SubsPushes  { get; } = new();
    public List<PendingGpodderAction>             ActionsSent { get; } = new();

    void IGpodderClient.Configure(string server, string username, string password) { }

    Task<bool> IGpodderClient.LoginAsync(string server, string username, string password)
        => Task.FromResult(LoginResult);

    Task IGpodderClient.RegisterDeviceAsync(string username, string deviceId)
        => Task.CompletedTask;

    Task<SubscriptionDelta> IGpodderClient.GetSubscriptionDeltaAsync(string username, string deviceId, long since)
        => Task.FromResult(NextDelta ?? new SubscriptionDelta([], [], NextTimestamp));

    Task<long> IGpodderClient.PushSubscriptionChangesAsync(string username, string deviceId, string[] add, string[] remove)
    {
        if (ThrowOnPush) throw new HttpRequestException("simulated");
        SubsPushes.Add((add, remove));
        return Task.FromResult(NextTimestamp);
    }

    Task<EpisodeActionsResult> IGpodderClient.GetEpisodeActionsAsync(string username, long since)
        => Task.FromResult(new EpisodeActionsResult([], NextTimestamp));

    Task<long> IGpodderClient.PushEpisodeActionsAsync(string username, IEnumerable<PendingGpodderAction> actions)
    {
        if (ThrowOnPush) throw new HttpRequestException("simulated");
        ActionsSent.AddRange(actions);
        return Task.FromResult(NextTimestamp);
    }

    void IDisposable.Dispose() { }
}
