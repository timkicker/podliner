using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using StuiPodcast.Infra.Sync;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

// Regression for issue #6 follow-up: Nextcloud users reported "nothing happens"
// on :sync commands. Root cause was silent login failures with no diagnostic
// info. These tests lock in the behavior that login failures surface the
// HTTP status code and — for 404 — a Nextcloud-specific hint.
public sealed class GpodderSyncLoginDiagnosticsTests
{
    static (GpodderSyncService svc, FakeGpodderClient client, string dir) MakeSetup()
    {
        var dir      = Path.Combine(Path.GetTempPath(), "podliner-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var data     = new AppData { NetworkOnline = true };
        var player   = new FakeAudioPlayer();
        var episodes = new FakeEpisodeStore();
        var feeds    = new FakeFeedStore();
        var queue    = new FakeQueueService();
        var pc       = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink(), episodes, queue);
        var store    = new GpodderStore(dir);
        store.Load();
        var client  = new FakeGpodderClient();
        var keyring = new FakeKeyring { AlwaysFail = true };
        var svc     = new GpodderSyncService(store, client, data, pc, episodes, feeds, keyring: keyring);
        return (svc, client, dir);
    }

    [Fact]
    public async Task LoginAsync_includes_http_status_code_in_error_message()
    {
        var (svc, client, dir) = MakeSetup();
        try
        {
            client.LoginResult    = false;
            client.LastLoginStatus = 401;
            client.LastLoginReason = "Unauthorized";

            var (ok, msg) = await svc.LoginAsync("https://gpodder.net", "user", "wrongpass");

            ok.Should().BeFalse();
            msg.Should().Contain("401");
            msg.Should().Contain("Unauthorized");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoginAsync_404_includes_nextcloud_hint()
    {
        var (svc, client, dir) = MakeSetup();
        try
        {
            client.LoginResult    = false;
            client.LastLoginStatus = 404;
            client.LastLoginReason = "Not Found";

            var (ok, msg) = await svc.LoginAsync("https://nextcloud.example.com", "user", "pass");

            ok.Should().BeFalse();
            msg.Should().Contain("404");
            msg.Should().Contain("Nextcloud", because: "users of Nextcloud gPodder-Sync need a clear pointer");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoginAsync_non_404_does_not_mention_nextcloud()
    {
        var (svc, client, dir) = MakeSetup();
        try
        {
            client.LoginResult    = false;
            client.LastLoginStatus = 500;
            client.LastLoginReason = "Server Error";

            var (ok, msg) = await svc.LoginAsync("https://gpodder.net", "user", "pass");

            ok.Should().BeFalse();
            msg.Should().Contain("500");
            msg.Should().NotContain("Nextcloud");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoginAsync_unknown_status_reports_fallback_message()
    {
        var (svc, client, dir) = MakeSetup();
        try
        {
            client.LoginResult    = false;
            client.LastLoginStatus = null; // no HTTP round-trip (e.g. DNS fail pre-resp)
            client.LastLoginReason = null;

            var (ok, msg) = await svc.LoginAsync("https://gpodder.net", "user", "pass");

            ok.Should().BeFalse();
            msg.Should().Contain("unknown");
        }
        finally { svc.Dispose(); Directory.Delete(dir, recursive: true); }
    }
}
