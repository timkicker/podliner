using FluentAssertions;
using Podliner.App.Tests.Fakes;
using Podliner.App.UI.Wiring;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.UI;

// Translates DownloadManager events into the badge in the window title and
// the download OSDs. The subtle part is that a running download emits a
// StatusChanged pulse roughly every 400ms: the badge has to follow those,
// but the OSD and the list refresh must fire only on real state changes.
// Like the playback bridge, every update is posted through
// Application.MainLoop.Invoke, so the tests run inside the headless harness
// and pump the queue instead of bypassing the real dispatch path.
[Collection(TuiCollection.Name)]
public sealed class UiDownloaderBridgeTests
{
    private const long MB = 1024L * 1024L;
    private static readonly Guid FeedId = Guid.Parse("dddd0000-0000-0000-0000-00000000000d");

    private sealed class Fixture : IDisposable
    {
        public readonly TuiHarness Tui = new();
        public readonly FakeUiShell Ui = new();
        public readonly AppData Data = new();
        public readonly FakeEpisodeStore Episodes = new();
        public readonly FakeDownloadManager Dlm = new();

        public Fixture() => UiDownloaderBridge.Attach(Dlm, Ui, Data, Episodes);

        // Drains the queue the bridge posts onto.
        public void Pump() => Tui.Pump();

        public void Dispose() => Tui.Dispose();

        public Guid Seed(string title = "Episode")
        {
            var ep = new Episode
            {
                Id = Guid.NewGuid(), FeedId = FeedId, Title = title,
                AudioUrl = "https://ex.test/a.mp3",
            };
            Episodes.Seed(ep);
            return ep.Id;
        }

        public string? Badge => Ui.DownloadBadges.LastOrDefault();
        public int OsdCount => Ui.OsdMessages.Count;
    }

    // Runs a manager action and drains the main-loop queue behind it.
    private static void Drive(Fixture f, Action act) { act(); f.Pump(); }

    // ── the badge ───────────────────────────────────────────────────────────

    [Fact]
    public void A_queued_download_puts_a_badge_up()
    {
        using var f = new Fixture();
        var id = f.Seed();

        Drive(f, () => f.Dlm.Set(id, DownloadState.Queued, total: 10 * MB));

        f.Badge.Should().NotBeNull();
    }

    [Fact]
    public void The_badge_counts_finished_against_total()
    {
        using var f = new Fixture();
        var a = f.Seed("A");
        var b = f.Seed("B");

        Drive(f, () => f.Dlm.Set(a, DownloadState.Running, 0, 10 * MB));
        Drive(f, () => f.Dlm.Set(b, DownloadState.Running, 0, 10 * MB));
        Drive(f, () => f.Dlm.Set(a, DownloadState.Done, 10 * MB, 10 * MB));

        f.Badge.Should().Contain("1/2");
    }

    [Fact]
    public void The_badge_carries_a_percentage()
    {
        using var f = new Fixture();
        var id = f.Seed();

        Drive(f, () => f.Dlm.Set(id, DownloadState.Running, 5 * MB, 10 * MB));

        f.Badge.Should().Contain("50%");
    }

    [Fact]
    public void The_first_running_update_shows_its_progress()
    {
        using var f = new Fixture();
        var id = f.Seed();

        Drive(f, () => f.Dlm.Set(id, DownloadState.Running, 9 * MB, 10 * MB));

        f.Badge.Should().Contain("90%");
    }

    [Fact]
    public void A_finished_download_reads_a_hundred_percent()
    {
        using var f = new Fixture();
        var id = f.Seed();
        Drive(f, () => f.Dlm.Set(id, DownloadState.Running, 3 * MB, 10 * MB));

        Drive(f, () => f.Dlm.Set(id, DownloadState.Done, 10 * MB, 10 * MB));

        f.Badge.Should().Contain("100%");
    }

    [Fact]
    public void Forgetting_the_last_download_clears_the_badge()
    {
        using var f = new Fixture();
        var id = f.Seed();
        Drive(f, () => f.Dlm.Set(id, DownloadState.Done, 10 * MB, 10 * MB));

        Drive(f, () => f.Dlm.Set(id, DownloadState.None));

        f.Ui.DownloadBadges.Last().Should().BeNull();
    }

    // ── pulses must not spam the UI ─────────────────────────────────────────

    [Fact]
    public void A_progress_pulse_does_not_raise_another_osd()
    {
        // Four pulses a second, each with its own OSD, would make the app
        // unusable during a download.
        using var f = new Fixture();
        var id = f.Seed();
        Drive(f, () => f.Dlm.Set(id, DownloadState.Running, 1 * MB, 100 * MB));
        var after = f.OsdCount;

        for (int i = 2; i <= 20; i++) Drive(f, () => f.Dlm.Pulse(id, i * MB, 100 * MB));

        f.OsdCount.Should().Be(after);
    }

    [Fact]
    public void A_real_state_change_does_raise_an_osd()
    {
        using var f = new Fixture();
        var id = f.Seed();
        Drive(f, () => f.Dlm.Set(id, DownloadState.Running, 1 * MB, 10 * MB));
        var after = f.OsdCount;

        Drive(f, () => f.Dlm.Set(id, DownloadState.Done, 10 * MB, 10 * MB));

        f.OsdCount.Should().BeGreaterThan(after);
    }

    [Fact]
    public void Rapid_pulses_are_throttled()
    {
        // The badge follows progress, but at most once every two seconds.
        // A multi-GB download pulses four times a second; repainting the
        // window title that often is wasted work.
        using var f = new Fixture();
        var id = f.Seed();
        Drive(f, () => f.Dlm.Set(id, DownloadState.Running, 1 * MB, 100 * MB));
        var badgeCount = f.Ui.DownloadBadges.Count;

        for (int i = 2; i <= 20; i++) Drive(f, () => f.Dlm.Pulse(id, i * MB, 100 * MB));

        f.Ui.DownloadBadges.Count.Should().Be(badgeCount);
    }

    // ── failures ────────────────────────────────────────────────────────────

    [Fact]
    public void A_failed_download_is_announced()
    {
        using var f = new Fixture();
        var id = f.Seed();
        Drive(f, () => f.Dlm.Set(id, DownloadState.Running, 1 * MB, 10 * MB));

        Drive(f, () => f.Dlm.Set(id, DownloadState.Failed, 1 * MB, 10 * MB));

        // The bridge uses terse markers rather than words: "!" for failed.
        f.Ui.OsdMessages.Should().Contain(m => m.Text.Contains("dl !"));
    }

    [Fact]
    public void A_failed_download_does_not_count_toward_progress()
    {
        using var f = new Fixture();
        var a = f.Seed("A");
        var b = f.Seed("B");
        Drive(f, () => f.Dlm.Set(a, DownloadState.Done, 10 * MB, 10 * MB));

        Drive(f, () => f.Dlm.Set(b, DownloadState.Failed, 3 * MB, 10 * MB));

        f.Badge.Should().Contain("100%");
    }

    // ── lifecycle ───────────────────────────────────────────────────────────

    [Fact]
    public void Attaching_starts_the_worker()
    {
        using var f = new Fixture();

        f.Dlm.EnsureRunningCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_null_shell_is_tolerated()
    {
        // Program.cs attaches before the shell exists in some orderings.
        var dlm = new FakeDownloadManager();
        var act = () =>
        {
            UiDownloaderBridge.Attach(dlm, null, new AppData(), new FakeEpisodeStore());
            dlm.Set(Guid.NewGuid(), DownloadState.Done, 1, 1);
        };

        act.Should().NotThrow();
    }
}
