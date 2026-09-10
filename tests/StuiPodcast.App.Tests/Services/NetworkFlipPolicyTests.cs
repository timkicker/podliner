using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

// The hysteresis that decides when podliner declares itself offline. A false
// "offline" hides every remote episode and stops downloads, so going offline
// deliberately needs more agreeing probes than coming back online.
public sealed class NetworkFlipPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    // Far enough past the seed that dwell never blocks a flip.
    private static DateTimeOffset Later(int seconds = 60) => T0.AddSeconds(seconds);

    private static NetworkFlipPolicy Seeded(bool online, TimeSpan? dwell = null)
    {
        var p = new NetworkFlipPolicy(dwell);
        p.Seed(online, T0);
        return p;
    }

    // ── seeding ─────────────────────────────────────────────────────────────

    [Fact]
    public void Seeding_online_starts_with_one_success()
    {
        var p = Seeded(online: true);

        p.Successes.Should().Be(1);
        p.Failures.Should().Be(0);
    }

    [Fact]
    public void Seeding_offline_starts_with_one_failure()
    {
        var p = Seeded(online: false);

        p.Failures.Should().Be(1);
        p.Successes.Should().Be(0);
    }

    // ── going offline ───────────────────────────────────────────────────────

    [Fact]
    public void One_failed_probe_does_not_go_offline()
    {
        var p = Seeded(online: true);

        p.Observe(currentState: true, probeOnline: false, Later()).Should().BeNull();
    }

    [Fact]
    public void Three_failed_probes_still_do_not_go_offline()
    {
        var p = Seeded(online: true);

        for (int i = 1; i <= 3; i++)
            p.Observe(true, false, Later()).Should().BeNull();
    }

    [Fact]
    public void The_fourth_failed_probe_goes_offline()
    {
        var p = Seeded(online: true);

        bool? flip = null;
        for (int i = 1; i <= NetworkFlipPolicy.FailsForOffline; i++)
            flip = p.Observe(true, false, Later());

        flip.Should().BeFalse();
    }

    [Fact]
    public void A_single_success_resets_the_failure_run()
    {
        var p = Seeded(online: true);
        p.Observe(true, false, Later());
        p.Observe(true, false, Later());
        p.Observe(true, false, Later());

        // One good probe in between means the next failure starts from one.
        p.Observe(true, true, Later());
        p.Failures.Should().Be(0);

        p.Observe(true, false, Later()).Should().BeNull();
    }

    // ── coming back online ──────────────────────────────────────────────────

    [Fact]
    public void One_successful_probe_does_not_come_back_online()
    {
        var p = Seeded(online: false);

        p.Observe(currentState: false, probeOnline: true, Later()).Should().BeNull();
    }

    [Fact]
    public void The_third_successful_probe_comes_back_online()
    {
        var p = Seeded(online: false);

        bool? flip = null;
        for (int i = 1; i <= NetworkFlipPolicy.SuccessesForOnline; i++)
            flip = p.Observe(false, true, Later());

        flip.Should().BeTrue();
    }

    [Fact]
    public void Coming_online_needs_fewer_probes_than_going_offline()
        => NetworkFlipPolicy.SuccessesForOnline.Should().BeLessThan(NetworkFlipPolicy.FailsForOffline);

    [Fact]
    public void A_single_failure_resets_the_success_run()
    {
        var p = Seeded(online: false);
        p.Observe(false, true, Later());
        p.Observe(false, true, Later());

        p.Observe(false, false, Later());
        p.Successes.Should().Be(0);

        p.Observe(false, true, Later()).Should().BeNull();
    }

    // ── dwell ───────────────────────────────────────────────────────────────

    [Fact]
    public void No_flip_happens_before_the_dwell_time_has_passed()
    {
        var p = Seeded(online: true, dwell: TimeSpan.FromSeconds(15));

        // Enough failures, but only 5s since the seed.
        for (int i = 1; i <= 10; i++)
            p.Observe(true, false, T0.AddSeconds(5)).Should().BeNull();
    }

    [Fact]
    public void The_flip_happens_once_the_dwell_time_has_passed()
    {
        var p = Seeded(online: true, dwell: TimeSpan.FromSeconds(15));

        for (int i = 1; i <= 10; i++) p.Observe(true, false, T0.AddSeconds(5));

        p.Observe(true, false, T0.AddSeconds(20)).Should().BeFalse();
    }

    [Fact]
    public void A_second_flip_has_to_wait_out_the_dwell_again()
    {
        var p = Seeded(online: true, dwell: TimeSpan.FromSeconds(15));

        for (int i = 1; i <= NetworkFlipPolicy.FailsForOffline; i++)
            p.Observe(true, false, T0.AddSeconds(20));

        // Flipped to offline at T0+20s. Successes right after must not flip
        // back, however many arrive.
        for (int i = 1; i <= 10; i++)
            p.Observe(false, true, T0.AddSeconds(25)).Should().BeNull();

        p.Observe(false, true, T0.AddSeconds(40)).Should().BeTrue();
    }

    // ── steady state ────────────────────────────────────────────────────────

    [Fact]
    public void A_stable_online_link_never_reports_a_flip()
    {
        var p = Seeded(online: true);

        for (int i = 1; i <= 50; i++)
            p.Observe(true, true, Later(i * 10)).Should().BeNull();
    }

    [Fact]
    public void A_stable_offline_link_never_reports_a_flip()
    {
        var p = Seeded(online: false);

        for (int i = 1; i <= 50; i++)
            p.Observe(false, false, Later(i * 10)).Should().BeNull();
    }

    [Fact]
    public void An_alternating_link_never_flips()
    {
        // Classic flapping wifi. Neither run ever reaches its threshold.
        var p = Seeded(online: true);

        for (int i = 1; i <= 40; i++)
            p.Observe(true, i % 2 == 0, Later(i * 10)).Should().BeNull();
    }
}

public sealed class NetworkProbeIntervalTests
{
    [Fact]
    public void Offline_is_probed_more_often_than_online()
    {
        var online = new AppData { NetworkOnline = true };
        var offline = new AppData { NetworkOnline = false };

        NetworkMonitor.NetProbeInterval(offline)
            .Should().BeLessThan(NetworkMonitor.NetProbeInterval(online));
    }

    [Fact]
    public void The_default_assumes_online()
        => NetworkMonitor.NetProbeInterval(null)
            .Should().Be(NetworkMonitor.NetProbeInterval(new AppData { NetworkOnline = true }));
}
