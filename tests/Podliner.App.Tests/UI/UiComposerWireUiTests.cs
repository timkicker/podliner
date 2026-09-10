using FluentAssertions;
using Podliner.App.Tests.Fakes;
using Podliner.App.UI;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.UI;

// UiComposer.WireUi is what Program.Main actually calls: it hooks every shell
// event to a service in one go. Until AppServices took interfaces this could
// not be constructed outside the real app, so nothing here was covered and a
// dropped subscription would only show up as a dead key in the running TUI.
//
// These wire the whole thing from fakes and then check that each user-facing
// event still reaches something.
[Collection(TuiCollection.Name)]
public sealed class UiComposerWireUiTests
{
    private static readonly Guid FeedId = Guid.Parse("eeee0000-0000-0000-0000-00000000000e");

    private sealed class Fixture : IDisposable
    {
        public readonly TuiHarness Tui = new();
        public readonly AppServicesBuilder B = new();
        public int TitleUpdates;

        public Fixture(Func<string, bool>? hasFeedWithUrl = null)
        {
            UiComposer.WireUi(
                ctx: B.Build(),
                save: B.Save,
                updateTitle: () => TitleUpdates++,
                hasFeedWithUrl: hasFeedWithUrl ?? (_ => false));
        }

        public FakeUiShell Ui => B.Ui;

        public Episode Seed(string title = "Episode")
        {
            var ep = new Episode
            {
                Id = Guid.NewGuid(), FeedId = FeedId, Title = title,
                AudioUrl = "https://ex.test/a.mp3",
                PubDate = DateTimeOffset.UtcNow.AddDays(-1),
            };
            B.Episodes.Seed(ep);
            return ep;
        }

        public void Dispose() { B.Dispose(); Tui.Dispose(); }
    }

    // ── it wires at all ─────────────────────────────────────────────────────

    [Fact]
    public void Wiring_the_whole_shell_does_not_throw()
    {
        var act = () => { using var f = new Fixture(); };

        act.Should().NotThrow();
    }

    [Fact]
    public void Wiring_twice_does_not_throw()
    {
        // Nothing in the app does this, but a double subscription should not
        // be a crash — it would be a duplicated side effect at worst.
        using var f = new Fixture();

        var act = () => UiComposer.WireUi(
            f.B.Build(), f.B.Save, () => { }, _ => false);

        act.Should().NotThrow();
    }

    // ── each event reaches a service ────────────────────────────────────────

    [Fact]
    public void Selecting_a_feed_renders_its_episodes()
    {
        using var f = new Fixture();
        f.Seed();
        f.Ui.SelectedFeedId = FeedId;

        f.Ui.RaiseSelectedFeedChanged();

        f.Ui.SetEpisodeCalls.Should().NotBeEmpty();
    }

    [Fact]
    public void Moving_the_episode_selection_persists()
    {
        using var f = new Fixture();
        var before = f.B.SaveCount;

        f.Ui.RaiseEpisodeSelectionChanged();

        f.B.SaveCount.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Searching_filters_the_list()
    {
        using var f = new Fixture();
        f.Seed("Rust Talk");
        f.Seed("Go Talk");
        f.Ui.SelectedFeedId = FeedId;

        f.Ui.RaiseSearchApplied("rust");

        f.Ui.SetEpisodeCalls.Last().Item2.Select(e => e.Title)
            .Should().Equal("Rust Talk");
    }

    [Fact]
    public void A_colon_command_reaches_the_router()
    {
        using var f = new Fixture();

        f.Ui.RaiseCommand(":osd hello");

        f.Ui.OsdMessages.Should().Contain(m => m.Text.Contains("hello"));
    }

    [Fact]
    public void An_unknown_command_is_reported_rather_than_swallowed()
    {
        using var f = new Fixture();

        f.Ui.RaiseCommand(":definitelynotacommand");

        f.Ui.OsdMessages.Should().Contain(m => m.Text.Contains("unknown"));
    }

    [Fact]
    public void A_queue_command_reaches_the_queue()
    {
        using var f = new Fixture();
        var ep = f.Seed();
        f.Ui.SelectedEpisode = ep;

        f.Ui.RaiseCommand(":queue add");

        f.B.Queue.Snapshot().Should().Contain(ep.Id);
    }

    [Fact]
    public void An_engine_command_reaches_the_switcher()
    {
        using var f = new Fixture();

        f.Ui.RaiseCommand(":engine mpv");

        f.B.EngineSwitches.Should().Contain(AudioEngine.Mpv);
    }

    [Fact]
    public void Toggling_the_played_marker_reaches_the_store()
    {
        using var f = new Fixture();
        var ep = f.Seed();
        f.Ui.SelectedEpisode = ep;
        var before = ep.ManuallyMarkedPlayed;

        f.Ui.RaiseTogglePlayedRequested();

        f.B.Episodes.Find(ep.Id)!.ManuallyMarkedPlayed.Should().Be(!before);
    }

    [Fact]
    public void Requesting_a_theme_toggle_changes_the_theme()
    {
        using var f = new Fixture();
        f.Ui.ThemeToggled.Should().BeFalse();

        f.Ui.RaiseToggleThemeRequested();

        f.Ui.ThemeToggled.Should().BeTrue();
    }

    // ── playing an episode ──────────────────────────────────────────────────

    // Play() is dispatched off the main loop, so give it a moment to land.
    private static void SettlePlayback(Fixture f)
    {
        for (int i = 0; i < 50 && f.B.Player.PlayCalls.Count == 0; i++)
        {
            f.Tui.Pump();
            Thread.Sleep(10);
        }
        f.Tui.Pump();
    }

    [Fact]
    public void Pressing_enter_plays_the_selected_episode()
    {
        using var f = new Fixture();
        var ep = f.Seed("Playable");
        f.Ui.SelectedEpisode = ep;
        f.B.Data.NetworkOnline = true;

        f.Ui.RaisePlaySelected();
        SettlePlayback(f);

        f.B.Player.LastPlayedUrl.Should().Be(ep.AudioUrl);
    }

    [Fact]
    public void Playing_marks_the_episode_as_now_playing()
    {
        using var f = new Fixture();
        var ep = f.Seed("Playable");
        f.Ui.SelectedEpisode = ep;
        f.B.Data.NetworkOnline = true;

        f.Ui.RaisePlaySelected();
        SettlePlayback(f);

        f.Ui.NowPlayingId.Should().Be(ep.Id);
    }

    [Fact]
    public void Playing_stamps_the_last_played_time()
    {
        using var f = new Fixture();
        var ep = f.Seed("Playable");
        ep.Progress.LastPlayedAt = null;
        f.Ui.SelectedEpisode = ep;
        f.B.Data.NetworkOnline = true;

        f.Ui.RaisePlaySelected();

        ep.Progress.LastPlayedAt.Should().NotBeNull();
    }

    [Fact]
    public void Playing_offline_without_a_download_says_so()
    {
        using var f = new Fixture();
        var ep = f.Seed("Not Downloaded");
        f.Ui.SelectedEpisode = ep;
        f.B.Data.NetworkOnline = false;

        f.Ui.RaisePlaySelected();

        f.B.Player.PlayCalls.Should().BeEmpty();
        f.Ui.OsdMessages.Should().Contain(m => m.Text.Contains("not downloaded"));
    }

    [Fact]
    public void Play_source_local_without_a_file_reports_no_source()
    {
        using var f = new Fixture();
        var ep = f.Seed("Remote Only");
        f.Ui.SelectedEpisode = ep;
        f.B.Data.NetworkOnline = true;
        f.B.Data.PlaySource = "local";

        f.Ui.RaisePlaySelected();

        f.B.Player.PlayCalls.Should().BeEmpty();
        // It must not blame the network: we are online, the setting is what
        // blocks playback.
        f.Ui.OsdMessages.Should().Contain(m => m.Text.Contains("play-source is local"));
        f.Ui.OsdMessages.Should().NotContain(m => m.Text.Contains("offline"));
    }

    [Fact]
    public void Pressing_enter_with_nothing_selected_is_harmless()
    {
        using var f = new Fixture();
        f.Ui.SelectedEpisode = null;

        var act = () => f.Ui.RaisePlaySelected();

        act.Should().NotThrow();
        f.B.Player.PlayCalls.Should().BeEmpty();
    }

    [Fact]
    public void Playing_does_not_taint_the_stored_audio_url()
    {
        // The resolved source is swapped into the episode for the duration of
        // Play() and must be restored, or a file:// URI ends up persisted.
        using var f = new Fixture();
        var ep = f.Seed("Playable");
        var original = ep.AudioUrl;
        f.Ui.SelectedEpisode = ep;
        f.B.Data.NetworkOnline = true;

        f.Ui.RaisePlaySelected();
        SettlePlayback(f);

        ep.AudioUrl.Should().Be(original);
    }

    // ── refresh path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Refreshing_while_online_runs_a_pass()
    {
        using var f = new Fixture();
        f.B.Data.NetworkOnline = true;

        await f.Ui.RaiseRefreshRequested();

        f.B.Feeds.RefreshAllCalls.Should().Be(1);
    }

    [Fact]
    public async Task Refreshing_while_offline_does_not()
    {
        using var f = new Fixture();
        f.B.Data.NetworkOnline = false;

        await f.Ui.RaiseRefreshRequested();

        f.B.Feeds.RefreshAllCalls.Should().Be(0);
    }

    // ── add feed ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Adding_a_feed_reaches_the_feed_service()
    {
        using var f = new Fixture();

        await f.Ui.RaiseAddFeedRequested("https://ex.test/new.xml");

        f.B.Feeds.AddedUrls.Should().Contain("https://ex.test/new.xml");
    }

    [Fact]
    public async Task Adding_a_feed_that_already_exists_is_refused()
    {
        using var f = new Fixture(hasFeedWithUrl: _ => true);

        await f.Ui.RaiseAddFeedRequested("https://ex.test/dupe.xml");

        f.B.Feeds.AddedUrls.Should().BeEmpty();
        f.Ui.OsdMessages.Should().NotBeEmpty();
    }
}
