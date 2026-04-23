using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

public sealed class GpodderSyncFlavorDetectTests : IDisposable
{
    readonly string _dir;
    readonly GpodderStore _store;
    readonly AppData _data = new();
    readonly FakeEpisodeStore _eps = new();
    readonly FakeFeedStore _feeds = new();
    readonly PlaybackCoordinator _playback;
    readonly FakeAudioPlayer _player = new();

    public GpodderSyncFlavorDetectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new GpodderStore(_dir);
        _store.Load();
        _data.NetworkOnline = true;
        _playback = new PlaybackCoordinator(_data, _player, () => Task.CompletedTask,
            new StuiPodcast.App.Debug.MemoryLogSink(), _eps, new FakeQueueService());
    }

    public void Dispose()
    {
        try { _playback.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Rigged factory: returns a fake client per flavor. Records which
    // flavors were asked for so the test can assert on detection order.
    sealed class RiggedFactory : IGpodderClientFactory
    {
        public List<GpodderFlavor> CreatedFlavors { get; } = new();
        public Dictionary<GpodderFlavor, Func<FakeGpodderClient>> Rig { get; } = new();

        public IGpodderClient Create(GpodderFlavor flavor)
        {
            CreatedFlavors.Add(flavor);
            return Rig.TryGetValue(flavor, out var f)
                ? f()
                : new FakeGpodderClient { LoginResult = false, LastLoginStatus = 404 };
        }
    }

    GpodderSyncService Make(RiggedFactory factory, IKeyring? keyring = null)
        => new(_store, factory, _data, _playback, _eps, _feeds,
               saveAsync: () => Task.CompletedTask,
               keyring: keyring ?? new FakeKeyring());

    // The service ctor always creates an initial client (for the stored
    // flavor, or GpodderNet for Auto) — tests assert on the ORDER of
    // probes after ctor, so we skip the ctor-time Auto/stored entry.
    static IEnumerable<GpodderFlavor> ProbeSequence(RiggedFactory f)
        => f.CreatedFlavors.Skip(1);

    [Fact]
    public async Task Gpodder_net_url_tries_gpoddernet_first_and_succeeds()
    {
        var factory = new RiggedFactory();
        factory.Rig[GpodderFlavor.GpodderNet] = () => new FakeGpodderClient { LoginResult = true, LastLoginStatus = 200 };
        factory.Rig[GpodderFlavor.Nextcloud]  = () => new FakeGpodderClient { LoginResult = false, LastLoginStatus = 404 };
        var svc = Make(factory);

        var (ok, _) = await svc.LoginAsync("https://gpodder.net", "alice", "pw");

        ok.Should().BeTrue();
        svc.Flavor.Should().Be(GpodderFlavor.GpodderNet);
        ProbeSequence(factory).Should().StartWith(new[] { GpodderFlavor.GpodderNet });
        ProbeSequence(factory).Should().NotContain(GpodderFlavor.Nextcloud);
    }

    [Fact]
    public async Task Nextcloud_url_hint_tries_nextcloud_first()
    {
        var factory = new RiggedFactory();
        factory.Rig[GpodderFlavor.Nextcloud] = () => new FakeGpodderClient { LoginResult = true, LastLoginStatus = 200 };
        var svc = Make(factory);

        var (ok, _) = await svc.LoginAsync("https://cloud.example.com/index.php", "alice", "pw");

        ok.Should().BeTrue();
        svc.Flavor.Should().Be(GpodderFlavor.Nextcloud);
        ProbeSequence(factory).First().Should().Be(GpodderFlavor.Nextcloud);
    }

    [Fact]
    public async Task Plain_host_with_only_nextcloud_support_falls_back_after_gpodder_404()
    {
        var factory = new RiggedFactory();
        factory.Rig[GpodderFlavor.GpodderNet] = () => new FakeGpodderClient { LoginResult = false, LastLoginStatus = 404 };
        factory.Rig[GpodderFlavor.Nextcloud]  = () => new FakeGpodderClient { LoginResult = true, LastLoginStatus = 200 };
        var svc = Make(factory);

        var (ok, _) = await svc.LoginAsync("https://cloud.example.com", "alice", "pw");

        ok.Should().BeTrue();
        svc.Flavor.Should().Be(GpodderFlavor.Nextcloud);
        ProbeSequence(factory).Should().ContainInOrder(GpodderFlavor.GpodderNet, GpodderFlavor.Nextcloud);
    }

    [Fact]
    public async Task Auth_401_on_first_flavor_stops_probe()
    {
        // Credentials are wrong — no point trying the second flavor, it
        // would 401 too. Return early with the accurate error.
        var factory = new RiggedFactory();
        factory.Rig[GpodderFlavor.GpodderNet] = () => new FakeGpodderClient { LoginResult = false, LastLoginStatus = 401, LastLoginReason = "Unauthorized" };
        factory.Rig[GpodderFlavor.Nextcloud]  = () => new FakeGpodderClient { LoginResult = true, LastLoginStatus = 200 };
        var svc = Make(factory);

        var (ok, msg) = await svc.LoginAsync("https://gpodder.net", "alice", "wrong");

        ok.Should().BeFalse();
        msg.Should().Contain("401");
        msg.Should().Contain("2FA"); // 401 hint
        // Only gpoddernet was probed; nextcloud wasn't (401 stops the fallback).
        ProbeSequence(factory).Should().NotContain(GpodderFlavor.Nextcloud);
    }

    [Fact]
    public async Task Stored_flavor_is_honoured_on_startup_without_probe()
    {
        _store.Current.Flavor = "nextcloud";
        _store.Save();

        var factory = new RiggedFactory();
        factory.Rig[GpodderFlavor.Nextcloud] = () => new FakeGpodderClient();
        // Service ctor creates the stored-flavor client directly.
        using var svc = Make(factory);

        svc.Flavor.Should().Be(GpodderFlavor.Nextcloud);
        factory.CreatedFlavors.Should().ContainSingle()
            .Which.Should().Be(GpodderFlavor.Nextcloud);
    }

    [Fact]
    public async Task Successful_login_persists_flavor_to_store()
    {
        var factory = new RiggedFactory();
        factory.Rig[GpodderFlavor.Nextcloud] = () => new FakeGpodderClient { LoginResult = true, LastLoginStatus = 200 };
        var svc = Make(factory);

        var (ok, _) = await svc.LoginAsync("https://cloud.example.com/index.php", "alice", "pw");
        ok.Should().BeTrue();

        _store.Current.Flavor.Should().Be("nextcloud");
    }

    [Fact]
    public async Task Logout_resets_flavor()
    {
        _store.Current.Flavor = "nextcloud";
        _store.Current.Username = "alice";
        _store.Current.Password = "pw";
        _store.Save();

        var factory = new RiggedFactory();
        factory.Rig[GpodderFlavor.Nextcloud] = () => new FakeGpodderClient();
        var svc = Make(factory);

        svc.Logout();

        _store.Current.Flavor.Should().BeNull();
        svc.Flavor.Should().Be(GpodderFlavor.Auto);
    }
}
