using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

// Regression for the UI-thread race fix: PullAsync/PushAsync now marshal feed
// mutations/reads through an injectable dispatcher. These tests verify the
// dispatcher is actually used for the critical code paths and that the default
// (null) falls back to synchronous execution.
public sealed class GpodderSyncDispatchTests
{
    static (GpodderSyncService svc, FakeGpodderClient client, FakeFeedStore feeds, string dir) Make(Func<Action, Task>? uiDispatch)
    {
        var dir      = Path.Combine(Path.GetTempPath(), "podliner-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var data     = new AppData { NetworkOnline = true };
        var player   = new FakeAudioPlayer();
        var episodes = new FakeEpisodeStore();
        var feeds    = new FakeFeedStore();
        var queue    = new FakeQueueService();
        var pc       = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink(), episodes, queue);
        var store    = new GpodderStore(dir);
        store.Load();
        store.Current.ServerUrl = "https://gpodder.net";
        store.Current.Username  = "user";
        store.Current.Password  = "pass";
        var client  = new FakeGpodderClient();
        var keyring = new FakeKeyring { AlwaysFail = true };
        var svc     = new GpodderSyncService(store, client, data, pc, episodes, feeds, keyring: keyring, uiDispatch: uiDispatch);
        return (svc, client, feeds, dir);
    }

    [Fact]
    public async Task PullAsync_invokes_dispatcher_for_Feed_mutations()
    {
        int dispatchCount = 0;
        Func<Action, Task> dispatcher = a => { dispatchCount++; a(); return Task.CompletedTask; };

        var (svc, client, _, dir) = Make(dispatcher);
        try
        {
            client.NextDelta = new SubscriptionDelta(
                Add: ["https://feed1.com/rss", "https://feed2.com/rss"],
                Remove: [],
                Timestamp: 100);

            await svc.PullAsync();

            dispatchCount.Should().BeGreaterThan(0,
                "Pull must marshal feed mutations to the UI thread via dispatcher");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PushAsync_invokes_dispatcher_for_Feed_snapshot()
    {
        int dispatchCount = 0;
        Func<Action, Task> dispatcher = a => { dispatchCount++; a(); return Task.CompletedTask; };

        var (svc, _, feeds, dir) = Make(dispatcher);
        try
        {
            feeds.Seed(new Feed { Id = Guid.NewGuid(), Title = "X", Url = "https://x.com/rss" });

            await svc.PushAsync();

            dispatchCount.Should().BeGreaterThan(0,
                "Push must snapshot feeds via dispatcher to avoid racing UI reads");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Null_dispatcher_falls_back_to_synchronous_execution()
    {
        // No dispatcher → default synchronous fallback. Pull should still work.
        var (svc, client, feeds, dir) = Make(uiDispatch: null);
        try
        {
            client.NextDelta = new SubscriptionDelta(
                Add: ["https://feed.com/rss"],
                Remove: [],
                Timestamp: 5);

            var (ok, _) = await svc.PullAsync();

            ok.Should().BeTrue();
            feeds.Snapshot().Should().ContainSingle(f => f.Url == "https://feed.com/rss");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Exception_from_dispatcher_propagates_as_pull_failure()
    {
        Func<Action, Task> throwing = _ => Task.FromException(new InvalidOperationException("nope"));
        var (svc, client, _, dir) = Make(throwing);
        try
        {
            client.NextDelta = new SubscriptionDelta(Add: ["https://x.com/rss"], Remove: [], Timestamp: 1);

            var (ok, msg) = await svc.PullAsync();

            ok.Should().BeFalse("PullAsync catches the dispatcher exception and reports it");
            msg.Should().Contain("pull error");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }
}
