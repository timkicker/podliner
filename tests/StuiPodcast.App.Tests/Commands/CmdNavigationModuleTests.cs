using FluentAssertions;
using StuiPodcast.App;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdNavigationModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly FakeQueueService _queue = new();
    private readonly PlaybackCoordinator _pc;
    private readonly NavigationUseCase _sut;

    public CmdNavigationModuleTests()
    {
        var player = new FakeAudioPlayer();
        _pc = new PlaybackCoordinator(_data, player, () => Task.CompletedTask, new MemoryLogSink(), _episodes, _queue);
        _sut = new NavigationUseCase(_ui, _data, _episodes, _pc);
    }

    private List<Episode> _feedEpisodes = new();

    private Guid MakeFeedWithEpisodes(int count, bool manuallyPlayed = false)
    {
        var fid = Guid.NewGuid();
        _feedEpisodes = new List<Episode>();
        for (int i = 0; i < count; i++)
        {
            var ep = new Episode
            {
                Id = Guid.NewGuid(),
                FeedId = fid,
                Title = $"ep{i}",
                AudioUrl = $"https://x.com/{i}.mp3",
                PubDate = DateTimeOffset.UtcNow.AddDays(-i),
                ManuallyMarkedPlayed = manuallyPlayed
            };
            _episodes.Seed(ep);
            _feedEpisodes.Add(ep);
        }
        _ui.SelectedFeedId = fid;
        return fid;
    }

    [Theory]
    [InlineData("top")]
    [InlineData("start")]
    public void Goto_top_start_selects_index_zero(string arg)
    {
        MakeFeedWithEpisodes(5);
        _sut.ExecGoto(new[] { arg });
        _ui.LastSelectedIndex.Should().Be(0);
    }

    [Theory]
    [InlineData("bottom")]
    [InlineData("end")]
    public void Goto_bottom_end_selects_last_index(string arg)
    {
        MakeFeedWithEpisodes(5);
        _sut.ExecGoto(new[] { arg });
        _ui.LastSelectedIndex.Should().Be(4);
    }

    [Fact]
    public void Goto_unknown_arg_does_not_change_selection()
    {
        MakeFeedWithEpisodes(3);
        _sut.ExecGoto(new[] { "banana" });
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void SelectAbsolute_clamps_to_list_range()
    {
        MakeFeedWithEpisodes(3);

        _sut.SelectAbsolute(99);
        _ui.LastSelectedIndex.Should().Be(2);

        _sut.SelectAbsolute(-5);
        _ui.LastSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void SelectAbsolute_on_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        _sut.SelectAbsolute(0);
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void SelectMiddle_picks_middle_index()
    {
        MakeFeedWithEpisodes(5);
        _sut.SelectMiddle();
        _ui.LastSelectedIndex.Should().Be(2);
    }

    [Fact]
    public void SelectMiddle_on_even_count_picks_upper_middle()
    {
        MakeFeedWithEpisodes(4);
        _sut.SelectMiddle();
        _ui.LastSelectedIndex.Should().Be(2);
    }

    [Fact]
    public void SelectMiddle_on_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        _sut.SelectMiddle();
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void SelectRelative_forward_moves_one_step()
    {
        MakeFeedWithEpisodes(5);
        _ui.SelectedEpisode = _feedEpisodes[0];
        _sut.SelectRelative(+1);
        _ui.LastSelectedIndex.Should().Be(1);
    }

    [Fact]
    public void SelectRelative_backward_moves_one_step()
    {
        MakeFeedWithEpisodes(5);
        _ui.SelectedEpisode = _feedEpisodes[2];
        _sut.SelectRelative(-1);
        _ui.LastSelectedIndex.Should().Be(1);
    }

    [Fact]
    public void SelectRelative_forward_at_end_clamps_to_last()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = _feedEpisodes[2];
        _sut.SelectRelative(+1);
        _ui.LastSelectedIndex.Should().Be(2);
    }

    [Fact]
    public void SelectRelative_backward_at_start_clamps_to_zero()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = _feedEpisodes[0];
        _sut.SelectRelative(-1);
        _ui.LastSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void SelectRelative_with_no_current_starts_at_index_zero()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = null;
        _sut.SelectRelative(+1);
        _ui.LastSelectedIndex.Should().Be(1);
    }

    [Fact]
    public void SelectRelative_on_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        _sut.SelectRelative(+1);
        _ui.LastSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void SelectRelative_with_playAfter_sets_now_playing()
    {
        MakeFeedWithEpisodes(3);
        _ui.SelectedEpisode = _feedEpisodes[0];
        _sut.SelectRelative(+1, playAfterSelect: true);

        _ui.NowPlayingId.Should().NotBeNull();
        _ui.LastShownDetails.Should().NotBeNull();
        _ui.LastWindowTitle.Should().Be(_feedEpisodes[1].Title);
    }

    [Fact]
    public void JumpUnplayed_no_feed_is_noop()
    {
        _ui.SelectedFeedId = null;
        _sut.JumpUnplayed(+1);
        _ui.NowPlayingId.Should().BeNull();
    }

    [Fact]
    public void JumpUnplayed_empty_list_is_noop()
    {
        _ui.SelectedFeedId = Guid.NewGuid();
        _sut.JumpUnplayed(+1);
        _ui.NowPlayingId.Should().BeNull();
    }

    [Fact]
    public void JumpUnplayed_all_played_is_noop()
    {
        MakeFeedWithEpisodes(3, manuallyPlayed: true);
        _sut.JumpUnplayed(+1);
        _ui.NowPlayingId.Should().BeNull();
    }

    [Fact]
    public void JumpUnplayed_picks_first_unplayed_forward()
    {
        MakeFeedWithEpisodes(3);
        _sut.JumpUnplayed(+1);

        _ui.NowPlayingId.Should().NotBeNull();
        _ui.LastShownDetails.Should().NotBeNull();
    }
}
