using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Mpris;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Tmds.DBus;
using Xunit;

namespace StuiPodcast.App.Tests.Mpris;

public sealed class MprisObjectPropertyTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    static (MprisObject obj, FakeAudioPlayer player, AppData data) MakeObject(
        bool isPlaying = false,
        Guid? episodeId = null)
    {
        var data   = new AppData();
        var player = new FakeAudioPlayer();
        player.State.IsPlaying  = isPlaying;
        player.State.EpisodeId  = episodeId;

        var pc  = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink());
        var obj = new MprisObject(data, player, pc);
        return (obj, player, data);
    }

    static async Task<IDictionary<string, object>> GetAllPlayerProps(MprisObject obj)
        => await ((IMprisPlayer)obj).GetAllAsync();

    static async Task<object> GetPlayerProp(MprisObject obj, string prop)
        => await ((IMprisPlayer)obj).GetAsync(prop);

    // ── PlaybackStatus ───────────────────────────────────────────────────────

    [Fact]
    public async Task PlaybackStatus_Stopped_when_no_episode()
    {
        var (obj, _, _) = MakeObject(isPlaying: false, episodeId: null);

        var status = await GetPlayerProp(obj, "PlaybackStatus");

        status.Should().Be("Stopped");
    }

    [Fact]
    public async Task PlaybackStatus_Playing_when_IsPlaying()
    {
        var (obj, _, _) = MakeObject(isPlaying: true, episodeId: Guid.NewGuid());

        var status = await GetPlayerProp(obj, "PlaybackStatus");

        status.Should().Be("Playing");
    }

    [Fact]
    public async Task PlaybackStatus_Paused_when_episode_loaded_but_not_playing()
    {
        var (obj, _, _) = MakeObject(isPlaying: false, episodeId: Guid.NewGuid());

        var status = await GetPlayerProp(obj, "PlaybackStatus");

        status.Should().Be("Paused");
    }

    // ── Metadata ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_NoTrack_when_no_episode()
    {
        var (obj, _, _) = MakeObject();

        var props    = await GetAllPlayerProps(obj);
        var metadata = (IDictionary<string, object>)props["Metadata"];

        metadata["mpris:trackid"].Should().Be(
            new ObjectPath("/org/mpris/MediaPlayer2/TrackList/NoTrack"));
    }

    [Fact]
    public async Task Metadata_trackid_is_ObjectPath_not_string()
    {
        var (obj, player, data) = MakeObject();
        var feedId = Guid.NewGuid();
        var ep = new Episode { FeedId = feedId, Title = "T", AudioUrl = "https://x.com/a.mp3", DurationMs = 60_000 };
        data.Feeds.Add(new Feed { Id = feedId, Title = "Feed" });
        data.Episodes.Add(ep);
        player.State.EpisodeId = ep.Id;

        var props    = await GetAllPlayerProps(obj);
        var metadata = (IDictionary<string, object>)props["Metadata"];

        metadata["mpris:trackid"].Should().BeOfType<ObjectPath>();
        metadata["mpris:trackid"].Should().NotBeOfType<string>();
    }

    [Fact]
    public async Task Metadata_artist_is_string_array_not_string()
    {
        var (obj, player, data) = MakeObject();
        var feedId = Guid.NewGuid();
        var ep = new Episode { FeedId = feedId, Title = "T", AudioUrl = "https://x.com/a.mp3" };
        data.Feeds.Add(new Feed { Id = feedId, Title = "My Podcast" });
        data.Episodes.Add(ep);
        player.State.EpisodeId = ep.Id;

        var props    = await GetAllPlayerProps(obj);
        var metadata = (IDictionary<string, object>)props["Metadata"];

        // D-Bus requires string[] — a plain string would cause a type error in clients
        metadata["xesam:artist"].Should().BeOfType<string[]>();
        var artists = (string[])metadata["xesam:artist"];
        artists.Should().ContainSingle().Which.Should().Be("My Podcast");
    }

    [Fact]
    public async Task Metadata_length_is_DurationMs_times_1000()
    {
        var (obj, player, data) = MakeObject();
        var feedId = Guid.NewGuid();
        var ep = new Episode { FeedId = feedId, Title = "T", AudioUrl = "https://x.com/a.mp3", DurationMs = 5_000 };
        data.Feeds.Add(new Feed { Id = feedId, Title = "F" });
        data.Episodes.Add(ep);
        player.State.EpisodeId = ep.Id;

        var props    = await GetAllPlayerProps(obj);
        var metadata = (IDictionary<string, object>)props["Metadata"];

        metadata["mpris:length"].Should().Be(5_000L * 1000L); // µs
    }

    // ── GetAllAsync keys ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_player_contains_all_required_keys()
    {
        var (obj, _, _) = MakeObject();

        var props = await GetAllPlayerProps(obj);

        props.Should().ContainKey("PlaybackStatus");
        props.Should().ContainKey("Metadata");
        props.Should().ContainKey("Volume");
        props.Should().ContainKey("Rate");
        props.Should().ContainKey("CanSeek");
        props.Should().ContainKey("CanPause");
        props.Should().ContainKey("CanControl");
        props.Should().ContainKey("CanGoNext");
        props.Should().ContainKey("CanGoPrevious");
        props.Should().ContainKey("Position");
    }

    // ── SetAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_Volume_calls_player_SetVolume_scaled()
    {
        var (obj, player, _) = MakeObject();

        await ((IMprisPlayer)obj).SetAsync("Volume", 0.5);

        player.LastSetVolume.Should().Be(50);
    }

    [Fact]
    public async Task SetAsync_Rate_calls_player_SetSpeed()
    {
        var (obj, player, _) = MakeObject();

        await ((IMprisPlayer)obj).SetAsync("Rate", 1.5);

        player.LastSetSpeed.Should().Be(1.5);
    }

    // ── Next/Previous smoke tests ─────────────────────────────────────────────

    [Fact]
    public async Task NextAsync_returns_completed_task_without_throw()
    {
        // Application.MainLoop is null in tests — the invoke is a no-op
        var (obj, _, _) = MakeObject();

        var act = async () => await ((IMprisPlayer)obj).NextAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PreviousAsync_returns_completed_task_without_throw()
    {
        var (obj, _, _) = MakeObject();

        var act = async () => await ((IMprisPlayer)obj).PreviousAsync();

        await act.Should().NotThrowAsync();
    }
}
