using FluentAssertions;
using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.App.Tests.Bootstrap;

// The seam between the persisted AppConfig and the runtime AppData. Every
// preference the user can change crosses it twice per session, and a field
// that survives one direction but not the other looks like "the setting
// doesn't stick" — which is exactly how the engine-preference bug behaved.
public sealed class AppBridgeTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigStore _config;
    private readonly LibraryStore _library;
    private readonly AppFacade _app;

    public AppBridgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _config = new ConfigStore(_dir);
        _library = new LibraryStore(_dir, subFolder: "", fileName: "library.json");
        _config.Load();
        _library.Load();
        _app = new AppFacade(_config, _library);
    }

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // In-memory round trip. ConfigStore only validates on Load, so this does
    // NOT exercise the normalisation rules — use RoundTripViaDisk for that.
    private AppData RoundTrip(AppData data)
    {
        AppBridge.SyncFromAppDataToFacade(data, _app);
        var restored = new AppData();
        AppBridge.SyncFromFacadeToAppData(_app, restored);
        return restored;
    }

    // The real shape of a restart: write appsettings.json, then load it from
    // scratch so ValidateAndNormalize actually runs. This is the path the
    // engine-preference bug hid in.
    private AppData RoundTripViaDisk(AppData data)
    {
        AppBridge.SyncFromAppDataToFacade(data, _app);
        _app.SaveNow();

        var reopened = new ConfigStore(_dir);
        reopened.Load();
        using var facade = new AppFacade(reopened, _library);

        var restored = new AppData();
        AppBridge.SyncFromFacadeToAppData(facade, restored);
        return restored;
    }

    // ── the engine preference, field by field ───────────────────────────────

    [Theory]
    [InlineData(AudioEngine.Auto)]
    [InlineData(AudioEngine.Vlc)]
    [InlineData(AudioEngine.Mpv)]
    [InlineData(AudioEngine.Ffplay)]
    [InlineData(AudioEngine.MediaFoundation)]
    public void Every_engine_survives_the_round_trip(AudioEngine engine)
    {
        // The regression that started this: ConfigStore rejected the wire
        // values for vlc and mediafoundation, so both silently became Auto.
        var restored = RoundTripViaDisk(new AppData { PreferredEngine = engine });

        restored.PreferredEngine.Should().Be(engine);
    }

    [Fact]
    public void The_engine_also_survives_a_real_save_and_reload()
    {
        AppBridge.SyncFromAppDataToFacade(new AppData { PreferredEngine = AudioEngine.Vlc }, _app);
        _app.SaveNow();

        var reopened = new ConfigStore(_dir);
        reopened.Load();
        using var facade = new AppFacade(reopened, _library);

        var restored = new AppData();
        AppBridge.SyncFromFacadeToAppData(facade, restored);

        restored.PreferredEngine.Should().Be(AudioEngine.Vlc);
    }

    // ── playback preferences ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(100)]
    public void Volume_survives(int volume)
        => RoundTrip(new AppData { Volume0_100 = volume }).Volume0_100.Should().Be(volume);

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(3.0)]
    public void Speed_survives(double speed)
        => RoundTrip(new AppData { Speed = speed }).Speed.Should().Be(speed);

    // ── ui preferences ──────────────────────────────────────────────────────

    // Every value the theme toggle can produce has to survive, or the choice
    // silently resets on the next launch — the same failure the engine
    // preference had.
    [Theory]
    [InlineData(ThemeMode.Base)]
    [InlineData(ThemeMode.MenuAccent)]
    [InlineData(ThemeMode.Native)]
    [InlineData(ThemeMode.User)]
    public void Every_theme_the_toggle_can_reach_survives(ThemeMode mode)
        => RoundTripViaDisk(new AppData { ThemePref = mode.ToString() })
            .ThemePref.Should().Be(mode.ToString());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_player_placement_survives(bool atTop)
        => RoundTrip(new AppData { PlayerAtTop = atTop }).PlayerAtTop.Should().Be(atTop);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_unplayed_filter_survives(bool on)
        => RoundTrip(new AppData { UnplayedOnly = on }).UnplayedOnly.Should().Be(on);

    // ── sorting ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pubdate", "desc")]
    [InlineData("title", "asc")]
    [InlineData("duration", "desc")]
    public void The_episode_sort_survives(string by, string dir)
    {
        var restored = RoundTrip(new AppData { SortBy = by, SortDir = dir });

        restored.SortBy.Should().Be(by);
        restored.SortDir.Should().Be(dir);
    }

    [Fact]
    public void The_feed_sort_survives()
    {
        var restored = RoundTrip(new AppData { FeedSortBy = "unplayed", FeedSortDir = "desc" });

        restored.FeedSortBy.Should().Be("unplayed");
        restored.FeedSortDir.Should().Be("desc");
    }

    // ── download directory ──────────────────────────────────────────────────

    [Fact]
    public void A_custom_download_directory_survives()
        => RoundTrip(new AppData { DownloadDir = "/mnt/media/podcasts" })
            .DownloadDir.Should().Be("/mnt/media/podcasts");

    [Fact]
    public void No_download_directory_stays_unset()
        => RoundTrip(new AppData { DownloadDir = null }).DownloadDir.Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_download_directory_normalises_to_unset(string dir)
    {
        // Otherwise a stray edit in appsettings.json makes the app try to
        // create a directory named " ".
        RoundTrip(new AppData { DownloadDir = dir }).DownloadDir.Should().BeNull();
    }

    // ── everything at once ──────────────────────────────────────────────────

    [Fact]
    public void A_fully_populated_state_survives_intact()
    {
        var original = new AppData
        {
            PreferredEngine = AudioEngine.Mpv,
            Volume0_100 = 33,
            Speed = 1.25,
            ThemePref = "Native",
            PlayerAtTop = true,
            UnplayedOnly = true,
            SortBy = "title",
            SortDir = "asc",
            FeedSortBy = "updated",
            FeedSortDir = "desc",
            DownloadDir = "/tmp/pods",
        };

        var restored = RoundTrip(original);

        restored.Should().BeEquivalentTo(original, o => o
            .Including(x => x.PreferredEngine)
            .Including(x => x.Volume0_100)
            .Including(x => x.Speed)
            .Including(x => x.ThemePref)
            .Including(x => x.PlayerAtTop)
            .Including(x => x.UnplayedOnly)
            .Including(x => x.SortBy)
            .Including(x => x.SortDir)
            .Including(x => x.FeedSortBy)
            .Including(x => x.FeedSortDir)
            .Including(x => x.DownloadDir));
    }

    [Fact]
    public void Round_tripping_twice_changes_nothing_further()
    {
        var once = RoundTrip(new AppData { PreferredEngine = AudioEngine.Ffplay, Volume0_100 = 12, Speed = 2.0 });
        var twice = RoundTrip(once);

        twice.PreferredEngine.Should().Be(once.PreferredEngine);
        twice.Volume0_100.Should().Be(once.Volume0_100);
        twice.Speed.Should().Be(once.Speed);
    }

    [Fact]
    public void Defaults_come_back_as_something_usable()
    {
        var restored = RoundTrip(new AppData());

        restored.PreferredEngine.Should().Be(AudioEngine.Auto);
        restored.Volume0_100.Should().BeInRange(0, 100);
        restored.Speed.Should().BeGreaterThan(0);
        restored.SortBy.Should().NotBeNullOrWhiteSpace();
    }
}
