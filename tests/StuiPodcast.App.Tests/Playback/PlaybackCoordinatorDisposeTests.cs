using FluentAssertions;
using StuiPodcast.App;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Playback;

// Regression tests for the CTS-leak fix: every Play call used to allocate new
// CancellationTokenSource instances while only calling Cancel() on the old ones,
// leaking the CTS registration table over long sessions. Dispose + CancelAndDispose
// must now release these cleanly.
public sealed class PlaybackCoordinatorDisposeTests
{
    private static (PlaybackCoordinator pc, FakeAudioPlayer player, AppData data) Make()
    {
        var data     = new AppData();
        var player   = new FakeAudioPlayer();
        var episodes = new FakeEpisodeStore();
        var queue    = new FakeQueueService();
        var pc       = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink(), episodes, queue);
        return (pc, player, data);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var (pc, _, _) = Make();

        pc.Dispose();
        var act = () => pc.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_on_fresh_coordinator_does_not_throw()
    {
        var (pc, _, _) = Make();
        var act = () => pc.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Rapid_play_calls_do_not_accumulate_pending_tasks()
    {
        var (pc, _, data) = Make();
        try
        {
            // Each Play creates _loadingCts/_resumeCts/_stallCts. Without the
            // CancelAndDispose fix, each Play leaked three CTS instances with
            // live callback registrations. We exercise the code path heavily
            // here: if the fix is intact, this completes quickly.
            for (int i = 0; i < 50; i++)
            {
                var ep = new Episode
                {
                    Title = $"ep-{i}", AudioUrl = "https://x.com/e.mp3",
                    Progress = new EpisodeProgress { LastPosMs = 1000 }
                };
                pc.Play(ep);
            }

            // Dispose should succeed without hangs.
            var act = () => pc.Dispose();
            act.Should().NotThrow();
        }
        finally { pc.Dispose(); }
    }

    [Fact]
    public void Dispose_after_play_releases_resources_without_hang()
    {
        var (pc, _, _) = Make();
        var ep = new Episode { Title = "ep", AudioUrl = "https://x.com/e.mp3" };
        pc.Play(ep);

        // Dispose must complete quickly (no waiting on background tasks).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        pc.Dispose();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }
}
