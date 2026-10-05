using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.Mpris;
using Podliner.App.Tests.Fakes;
using Podliner.Core;
using Tmds.DBus;
using Xunit;

namespace Podliner.App.Tests.Mpris;

// #33: Metadata stayed NoTrack while an episode played, because MPRIS read
// PlayerState.EpisodeId and nothing in the app ever set it. These tests play
// through the coordinator, the way the app does, and never touch the player
// state's episode.
public sealed class MprisFollowsCoordinatorTests
{
    static readonly Guid FeedId = Guid.Parse("33333333-0000-0000-0000-000000000033");

    static (MprisObject obj, FakeAudioPlayer player, PlaybackCoordinator pc, Episode ep) Playing()
    {
        var data     = new AppData();
        var player   = new FakeAudioPlayer();
        var episodes = new FakeEpisodeStore();
        var feeds    = new FakeFeedStore();
        var ep = new Episode
        {
            Id = Guid.NewGuid(), FeedId = FeedId, Title = "Episode 12",
            AudioUrl = "https://ex.test/12.mp3", DurationMs = 90_000,
        };
        feeds.Seed(new Feed { Id = FeedId, Title = "Some Show" });
        episodes.Seed(ep);

        var pc = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink(), episodes, new FakeQueueService());
        pc.Play(ep);
        // the coordinator hands the url to the engine on the thread pool, and
        // takes ticks once the engine's Play has returned
        SpinWait.SpinUntil(() => pc.EngineHasTaken, 15000);
        return (new MprisObject(data, player, pc, episodes, feeds), player, pc, ep);
    }

    static async Task<object> Prop(MprisObject obj, string name) => await ((IMprisPlayer)obj).GetAsync(name);

    [Fact]
    public async Task Metadata_describes_the_episode_the_coordinator_plays()
    {
        var (obj, player, _, ep) = Playing();
        player.State.IsPlaying = true;

        var meta = (IDictionary<string, object>)await Prop(obj, "Metadata");

        meta["mpris:trackid"].Should().Be(new ObjectPath($"/org/podliner/track/{ep.Id:N}"));
        meta["xesam:title"].Should().Be("Episode 12");
        meta["xesam:album"].Should().Be("Some Show");
        meta["xesam:artist"].Should().BeEquivalentTo(new[] { "Some Show" });
        meta["mpris:length"].Should().Be(90_000L * 1000L);
    }

    [Fact]
    public async Task A_paused_episode_reports_Paused_not_Stopped()
    {
        var (obj, player, _, _) = Playing();
        player.State.IsPlaying = false;

        (await Prop(obj, "PlaybackStatus")).Should().Be("Paused");
    }

    [Fact]
    public async Task Length_falls_back_to_what_the_engine_reports()
    {
        var (obj, player, pc, ep) = Playing();
        ep.DurationMs = 0;                                  // feed did not say
        player.State.IsPlaying = true;
        player.State.Position = TimeSpan.FromSeconds(5);
        player.State.Length = TimeSpan.FromMinutes(42);
        pc.PersistProgressTick(player.State, _ => { });

        var meta = (IDictionary<string, object>)await Prop(obj, "Metadata");

        meta["mpris:length"].Should().Be((long)TimeSpan.FromMinutes(42).TotalMicroseconds);
    }
}
