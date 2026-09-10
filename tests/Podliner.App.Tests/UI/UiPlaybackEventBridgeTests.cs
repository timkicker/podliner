using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.Tests.Fakes;
using Podliner.App.UI.Wiring;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.UI;

// Translates PlaybackCoordinator events into UI updates: the player bar, the
// window title, the loading overlay and the chapter highlight. This is what
// makes the UI look alive during playback, and it had no coverage.
// The bridge marshals every update through Application.MainLoop.Invoke, so
// the tests run inside the headless harness and pump the queue rather than
// bypassing the real dispatch path.
[Collection(TuiCollection.Name)]
public sealed class UiPlaybackEventBridgeTests
{
    private static readonly Guid FeedId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private sealed class Fixture : IDisposable
    {
        public readonly TuiHarness Tui = new();
        public readonly FakeUiShell Ui = new();
        public readonly AppData Data = new();
        public readonly FakeAudioPlayer Player = new();
        public readonly FakeEpisodeStore Episodes = new();
        public readonly FakeQueueService Queue = new();
        public readonly PlaybackCoordinator Playback;

        public Fixture()
        {
            Playback = new PlaybackCoordinator(
                Data, Player, () => Task.CompletedTask, new MemoryLogSink(), Episodes, Queue);

            UiPlaybackEventBridge.Wire(Ui, Data, Player, Playback, Episodes);
        }

        // Drains the main-loop queue the bridge posts onto.
        public void Pump() => Tui.Pump();

        public void Dispose()
        {
            Playback.Dispose();
            Tui.Dispose();
        }

        public Episode Seed(string title = "Episode")
        {
            var ep = new Episode
            {
                Id = Guid.NewGuid(), FeedId = FeedId, Title = title,
                AudioUrl = "https://ex.test/a.mp3", DurationMs = 600_000,
                PubDate = DateTimeOffset.UtcNow.AddDays(-1),
            };
            Episodes.Seed(ep);
            return ep;
        }

        public void TickAndPump(long posMs, long lenMs, bool playing = true)
        {
            Tick(posMs, lenMs, playing);
            Pump();
        }

        public void Tick(long posMs, long lenMs, bool playing = true)
            => Playback.PersistProgressTick(
                new PlayerState
                {
                    IsPlaying = playing,
                    Position = TimeSpan.FromMilliseconds(posMs),
                    Length = TimeSpan.FromMilliseconds(lenMs),
                    Speed = 1.0,
                },
                _ => { });
    }

    // ── player bar ──────────────────────────────────────────────────────────

    [Fact]
    public void A_snapshot_updates_the_player_bar()
    {
        using var f = new Fixture();
        var ep = f.Seed();

        f.Playback.Play(ep);
        f.Pump();

        f.Ui.PlayerSnapshots.Should().NotBeEmpty();
    }

    [Fact]
    public void The_player_bar_is_told_the_current_volume()
    {
        // The bar reads the engine's volume, not the persisted preference.
        using var f = new Fixture();
        f.Player.SetVolume(37);
        var ep = f.Seed();

        f.Playback.Play(ep);
        f.Pump();

        f.Ui.PlayerSnapshots.Last().Volume.Should().Be(37);
    }

    [Fact]
    public void Progress_ticks_reach_the_player_bar()
    {
        using var f = new Fixture();
        var ep = f.Seed();
        f.Playback.Play(ep);
        f.Pump();
        var before = f.Ui.PlayerSnapshots.Count;

        f.TickAndPump(30_000, 600_000);

        f.Ui.PlayerSnapshots.Count.Should().BeGreaterThan(before);
        f.Ui.PlayerSnapshots.Last().Snap.Position.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void The_active_row_progress_is_refreshed()
    {
        using var f = new Fixture();
        var ep = f.Seed();
        f.Playback.Play(ep);
        f.Pump();

        f.TickAndPump(30_000, 600_000);

        f.Ui.LastActiveProgressSnap.Should().NotBeNull();
        f.Ui.LastActiveProgressSnap!.Value.EpisodeId.Should().Be(ep.Id);
    }

    // ── window title ────────────────────────────────────────────────────────

    [Fact]
    public void Playing_puts_the_episode_title_in_the_window_title()
    {
        // The bridge only titles the episode the shell already reports as
        // now-playing; UiPlaybackWiring sets that when the user hits play.
        using var f = new Fixture();
        var ep = f.Seed("Some Episode");
        f.Ui.SetNowPlaying(ep.Id);

        f.Playback.Play(ep);
        f.Pump();
        f.TickAndPump(1_000, 600_000);

        f.Ui.LastWindowTitle.Should().Contain("Some Episode");
    }

    [Fact]
    public void Being_offline_is_marked_in_the_window_title()
    {
        using var f = new Fixture();
        f.Data.NetworkOnline = false;
        var ep = f.Seed("Some Episode");
        f.Ui.SetNowPlaying(ep.Id);

        f.Playback.Play(ep);
        f.Pump();
        f.TickAndPump(1_000, 600_000);

        f.Ui.LastWindowTitle.Should().Contain("OFFLINE");
    }

    [Fact]
    public void The_title_is_not_rewritten_on_every_tick()
    {
        // The tick runs 4x/sec; re-resolving and re-setting the same string
        // each time is pure churn.
        using var f = new Fixture();
        var ep = f.Seed("Some Episode");
        f.Ui.SetNowPlaying(ep.Id);
        f.Playback.Play(ep);
        f.Pump();
        f.TickAndPump(1_000, 600_000);

        var titleSets = f.Ui.WindowTitleCalls;
        for (int i = 2; i < 10; i++) f.TickAndPump(i * 1_000, 600_000);

        f.Ui.WindowTitleCalls.Should().Be(titleSets);
    }

    // ── loading overlay ─────────────────────────────────────────────────────

    [Fact]
    public void Starting_playback_shows_the_loading_overlay()
    {
        using var f = new Fixture();
        var ep = f.Seed();

        f.Playback.Play(ep);
        f.Pump();

        f.Ui.LoadingCalls.Should().Contain(c => c.On);
    }

    [Fact]
    public void The_overlay_clears_once_the_position_advances()
    {
        using var f = new Fixture();
        var ep = f.Seed();
        f.Playback.Play(ep);
        f.Pump();

        f.TickAndPump(1_000, 600_000);

        f.Ui.LoadingCalls.Last().On.Should().BeFalse();
    }

    [Fact]
    public void A_tick_at_zero_keeps_the_overlay_up()
    {
        // LibVLC reports IsPlaying while still buffering.
        using var f = new Fixture();
        var ep = f.Seed();
        f.Playback.Play(ep);
        f.Pump();

        f.TickAndPump(0, 600_000);

        f.Ui.LoadingCalls.Last().On.Should().BeTrue();
    }

    [Fact]
    public void Reaching_the_end_clears_the_overlay()
    {
        using var f = new Fixture();
        var ep = f.Seed();
        ep.DurationMs = 10_000;
        f.Playback.Play(ep);
        f.Pump();
        f.TickAndPump(1_000, 10_000);

        f.TickAndPump(10_000, 10_000, playing: false);

        f.Ui.LoadingCalls.Last().On.Should().BeFalse();
    }

    // ── engine capabilities ─────────────────────────────────────────────────

    [Fact]
    public void The_speed_buttons_follow_the_engine_capability()
    {
        using var f = new Fixture();
        var ep = f.Seed();

        f.Playback.Play(ep);
        f.Pump();
        f.TickAndPump(1_000, 600_000);

        // FakeAudioPlayer advertises Speed support.
        f.Ui.SpeedEnabled.Should().BeTrue();
    }
}
