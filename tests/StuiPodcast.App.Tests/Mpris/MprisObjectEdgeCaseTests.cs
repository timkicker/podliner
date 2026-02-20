using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Mpris;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Tmds.DBus;
using Xunit;

namespace StuiPodcast.App.Tests.Mpris;

public sealed class MprisObjectEdgeCaseTests
{
    static (MprisObject obj, FakeAudioPlayer player, AppData data) MakeObject()
    {
        var data   = new AppData();
        var player = new FakeAudioPlayer();
        var pc     = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink());
        return (new MprisObject(data, player, pc), player, data);
    }

    // ── Metadata edge cases ───────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_has_partial_data_when_episode_exists_but_feed_is_missing()
    {
        var (obj, player, data) = MakeObject();
        // Episode exists but its feed is NOT in data.Feeds
        var ep = new Episode { FeedId = Guid.NewGuid(), Title = "Orphan", AudioUrl = "https://x.com/a.mp3", DurationMs = 10_000 };
        data.Episodes.Add(ep);
        player.State.EpisodeId = ep.Id;

        var props    = await ((IMprisPlayer)obj).GetAllAsync();
        var metadata = (IDictionary<string, object>)props["Metadata"];

        // Core metadata is present
        metadata.Should().ContainKey("xesam:title");
        metadata["xesam:title"].Should().Be("Orphan");
        // Feed-derived fields are absent (no album/artist without a feed)
        metadata.Should().NotContainKey("xesam:album");
        metadata.Should().NotContainKey("xesam:artist");
    }

    // ── Volume scaling boundaries ─────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_Volume_zero_calls_SetVolume_0()
    {
        var (obj, player, _) = MakeObject();

        await ((IMprisPlayer)obj).SetAsync("Volume", 0.0);

        player.LastSetVolume.Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_Volume_one_calls_SetVolume_100()
    {
        var (obj, player, _) = MakeObject();

        await ((IMprisPlayer)obj).SetAsync("Volume", 1.0);

        player.LastSetVolume.Should().Be(100);
    }

    // ── MediaPlayer2 interface ────────────────────────────────────────────────

    [Fact]
    public async Task MediaPlayer2_GetAllAsync_contains_Identity_and_CanQuit()
    {
        var (obj, _, _) = MakeObject();

        var props = await ((IMprisMediaPlayer2)obj).GetAllAsync();

        props.Should().ContainKey("Identity");
        props["Identity"].Should().Be("Podliner");
        props.Should().ContainKey("CanQuit");
        props["CanQuit"].Should().Be(true);
        props.Should().ContainKey("CanRaise");
        props["CanRaise"].Should().Be(false);
    }

    // ── Signal emission ───────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyPlayerPropertiesChanged_fires_PlayerPropertiesChanged_event()
    {
        var (obj, _, _) = MakeObject();

        var tcs = new TaskCompletionSource<PropertyChanges>();
        obj.PlayerPropertiesChanged += changes => tcs.TrySetResult(changes);

        obj.NotifyPlayerPropertiesChanged(new Dictionary<string, object>
        {
            ["PlaybackStatus"] = "Playing"
        });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        received.Changed.Select(kvp => kvp.Key).Should().Contain("PlaybackStatus");
    }

    [Fact]
    public async Task NotifySeeked_fires_SeekedSignal_event()
    {
        var (obj, _, _) = MakeObject();

        var tcs = new TaskCompletionSource<long>();
        obj.SeekedSignal += posUs => tcs.TrySetResult(posUs);

        obj.NotifySeeked(123_456_789L);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        received.Should().Be(123_456_789L);
    }

    [Fact]
    public async Task NotifyPlayerPropertiesChanged_no_op_for_empty_dict()
    {
        var (obj, _, _) = MakeObject();

        bool eventFired = false;
        obj.PlayerPropertiesChanged += _ => eventFired = true;

        // Should silently ignore and not fire the event
        obj.NotifyPlayerPropertiesChanged(new Dictionary<string, object>());

        // Give Task.Run a brief window; if it fires, the flag would be set
        await Task.Delay(50);
        eventFired.Should().BeFalse();
    }
}
