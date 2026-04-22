using FluentAssertions;
using StuiPodcast.App;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdNavigationModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly PlaybackCoordinator _pc;

    public CmdNavigationModuleTests()
    {
        var player = new FakeAudioPlayer();
        _pc = new PlaybackCoordinator(_data, player, () => Task.CompletedTask, new MemoryLogSink());
    }

    private Guid MakeFeedWithEpisodes(int count, bool manuallyPlayed = false)
    {
        var fid = Guid.NewGuid();
        for (int i = 0; i < count; i++)
            _data.Episodes.Add(new Episode
            {
                FeedId = fid,
                Title = $"ep{i}",
                AudioUrl = $"https://x.com/{i}.mp3",
                PubDate = DateTimeOffset.UtcNow.AddDays(-i),
                ManuallyMarkedPlayed = manuallyPlayed
            });
        _ui.SelectedFeedId = fid;
        return fid;
    }

    // ── ExecGoto ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("top")]
    [InlineData("start")]
    public void Goto_top_start_selects_index_zero(string arg)
    {
        MakeFeedWithEpisodes(5);
        CmdNavigationModule.ExecGoto(new[] { arg }, _ui, _data);
        _ui.LastSelectedIndex.Should().Be(0);
    }

    [Theory]
    [InlineData("bottom")]
    [InlineData("end")]
    public void Goto_bottom_end_selects_last_index(string arg)
    {
        MakeFeedWithEpisodes(5);
        CmdNavigationModule.ExecGoto(new[] { arg }, _ui, _data);
        _ui.LastSelectedIndex.Should().Be(4);
    }

    [Fact]
    public void Goto_unknown_arg_does_not_change_selection()
    {
        MakeFeedWithEpisodes(3);
        CmdNavigationModule.ExecGoto(new[] { "banana" }, _ui, _data);
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    // ── SelectAbsolute ───────────────────────────────────────────────────────

    [Fact]
    public void SelectAbsolute_clamps_to_list_range()
    {
        MakeFeedWithEpisodes(3);

        CmdNavigationModule.SelectAbsolute(99, _ui, _data);
        _ui.LastSelectedIndex.Should().Be(2);

        CmdNavigationModule.SelectAbsolute(-5, _ui, _data);
        _ui.LastSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void SelectAbsolute_on_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        CmdNavigationModule.SelectAbsolute(0, _ui, _data);
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    // ── SelectMiddle ─────────────────────────────────────────────────────────

    [Fact]
    public void SelectMiddle_picks_middle_index()
    {
        MakeFeedWithEpisodes(5);
        CmdNavigationModule.SelectMiddle(_ui, _data);
        _ui.LastSelectedIndex.Should().Be(2); // 5/2 == 2
    }

    [Fact]
    public void SelectMiddle_on_even_count_picks_upper_middle()
    {
        MakeFeedWithEpisodes(4);
        CmdNavigationModule.SelectMiddle(_ui, _data);
        _ui.LastSelectedIndex.Should().Be(2); // 4/2 == 2
    }

    [Fact]
    public void SelectMiddle_on_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        CmdNavigationModule.SelectMiddle(_ui, _data);
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    // ── SelectRelative ──────────────────────────────────────────────────────

    [Fact]
    public void SelectRelative_forward_moves_one_step()
    {
        MakeFeedWithEpisodes(5);
        _ui.SelectedEpisode = _data.Episodes[0];

        CmdNavigationModule.SelectRelative(+1, _ui, _data);

        _ui.LastSelectedIndex.Should().Be(1);
    }

    [Fact]
    public void SelectRelative_backward_moves_one_step()
    {
        MakeFeedWithEpisodes(5);
        _ui.SelectedEpisode = _data.Episodes[2];

        CmdNavigationModule.SelectRelative(-1, _ui, _data);

        _ui.LastSelectedIndex.Should().Be(1);
    }

    [Fact]
    public void SelectRelative_forward_at_end_clamps_to_last()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = _data.Episodes[2];

        CmdNavigationModule.SelectRelative(+1, _ui, _data);

        _ui.LastSelectedIndex.Should().Be(2);
    }

    [Fact]
    public void SelectRelative_backward_at_start_clamps_to_zero()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = _data.Episodes[0];

        CmdNavigationModule.SelectRelative(-1, _ui, _data);

        _ui.LastSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void SelectRelative_with_no_current_starts_at_index_zero()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = null;

        CmdNavigationModule.SelectRelative(+1, _ui, _data);

        // idx defaults to 0, then +1 → 1
        _ui.LastSelectedIndex.Should().Be(1);
    }

    [Fact]
    public void SelectRelative_on_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        CmdNavigationModule.SelectRelative(+1, _ui, _data);
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void SelectRelative_with_playAfter_sets_now_playing()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = _data.Episodes[0];

        CmdNavigationModule.SelectRelative(+1, _ui, _data, playAfterSelect: true, playback: _pc);

        _ui.NowPlayingId.Should().NotBeNull();
        _ui.LastShownDetails.Should().NotBeNull();
        _ui.LastWindowTitle.Should().Be(_data.Episodes[1].Title);
    }

    // ── JumpUnplayed ────────────────────────────────────────────────────────

    [Fact]
    public void JumpUnplayed_no_feed_is_noop()
    {
        _ui.SelectedFeedId = null;
        CmdNavigationModule.JumpUnplayed(+1, _ui, _pc, _data);
        _ui.NowPlayingId.Should().BeNull();
    }

    [Fact]
    public void JumpUnplayed_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        CmdNavigationModule.JumpUnplayed(+1, _ui, _pc, _data);
        _ui.NowPlayingId.Should().BeNull();
    }

    [Fact]
    public void JumpUnplayed_all_played_is_noop()
    {
        MakeFeedWithEpisodes(3, manuallyPlayed: true);
        CmdNavigationModule.JumpUnplayed(+1, _ui, _pc, _data);
        _ui.NowPlayingId.Should().BeNull();
    }

    [Fact]
    public void JumpUnplayed_picks_first_unplayed_forward()
    {
        MakeFeedWithEpisodes(3);
        CmdNavigationModule.JumpUnplayed(+1, _ui, _pc, _data);

        _ui.NowPlayingId.Should().NotBeNull();
        _ui.LastShownDetails.Should().NotBeNull();
    }
}
