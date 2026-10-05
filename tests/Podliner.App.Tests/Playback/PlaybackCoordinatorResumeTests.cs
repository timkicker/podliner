using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.Tests.Fakes;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.Playback;

// #39 and what came up around it. The saved position reached the engine as
// a seek 350 ms after Play, which every engine drops while a stream is still
// opening (mpv answers "error running command", VLC is not seekable yet, the
// built-in engine has not read the header), so streamed episodes started at
// 0:00. And every tick wrote the engine's position into the episode, also
// before the engine had taken it: 0 while loading, or the position of the
// episode before, which marked a 12 s episode played after a switch from 40:00.
public sealed class PlaybackCoordinatorResumeTests
{
    static (PlaybackCoordinator pc, FakeAudioPlayer player, FakeEpisodeStore episodes) Make()
    {
        var player = new FakeAudioPlayer();
        var episodes = new FakeEpisodeStore();
        var pc = new PlaybackCoordinator(new AppData(), player, () => Task.CompletedTask, new MemoryLogSink(),
                                         episodes, new FakeQueueService());
        return (pc, player, episodes);
    }

    static Episode Ep(long durationMs, long lastPosMs = 0) => new()
    {
        Id = Guid.NewGuid(), FeedId = Guid.NewGuid(), Title = "Ep", AudioUrl = "https://ex.test/ep.mp3",
        DurationMs = durationMs, PubDate = DateTimeOffset.UtcNow,
        Progress = new EpisodeProgress { LastPosMs = lastPosMs },
    };

    static void Tick(PlaybackCoordinator pc, FakeAudioPlayer p) => pc.PersistProgressTick(p.State, _ => { });

    static void WaitForPlay(FakeAudioPlayer p)
        => SpinWait.SpinUntil(() => { lock (p.PlayCalls) return p.PlayCalls.Count > 0; }, 15000);   // a busy windows runner took over 3 s

    [Fact]
    public void The_saved_position_goes_to_the_engine_with_the_play()
    {
        var (pc, player, _) = Make();
        using var _pc = pc;

        pc.Play(Ep(durationMs: 3_600_000, lastPosMs: 191_673));
        WaitForPlay(player);

        player.PlayCalls.Single().StartMs.Should().Be(191_673);
    }

    [Fact]
    public void Ticks_before_the_engine_has_taken_the_episode_do_not_touch_it()
    {
        var (pc, player, _) = Make();
        using var _pc = pc;
        // the engine still plays the episode before, at 40:00
        player.State.Position = TimeSpan.FromMinutes(40);
        player.State.Length = TimeSpan.FromMinutes(60);
        player.State.IsPlaying = true;
        player.PlayGate = new ManualResetEventSlim(false);
        var shortEp = Ep(durationMs: 12_000);

        pc.Play(shortEp);
        WaitForPlay(player);
        Tick(pc, player);
        Tick(pc, player);
        player.PlayGate.Set();

        shortEp.Progress.LastPosMs.Should().Be(0);
        shortEp.ManuallyMarkedPlayed.Should().BeFalse();
    }

    [Fact]
    public void While_it_loads_the_saved_position_is_kept()
    {
        var (pc, player, _) = Make();
        using var _pc = pc;
        var ep = Ep(durationMs: 3_600_000, lastPosMs: 191_673);

        pc.Play(ep);
        WaitForPlay(player);
        Thread.Sleep(100);              // Play has returned; the stream is not open yet
        player.State.Position = TimeSpan.Zero;
        player.State.IsPlaying = false;
        Tick(pc, player);
        Tick(pc, player);

        ep.Progress.LastPosMs.Should().Be(191_673);
    }

    [Fact]
    public void Once_it_plays_the_position_is_kept_up_to_date()
    {
        var (pc, player, _) = Make();
        using var _pc = pc;
        var ep = Ep(durationMs: 3_600_000, lastPosMs: 191_673);

        pc.Play(ep);
        WaitForPlay(player);
        Thread.Sleep(100);
        player.State.Position = TimeSpan.FromSeconds(200);
        player.State.Length = TimeSpan.FromHours(1);
        player.State.IsPlaying = true;
        Tick(pc, player);

        ep.Progress.LastPosMs.Should().Be(200_000);
    }
}
