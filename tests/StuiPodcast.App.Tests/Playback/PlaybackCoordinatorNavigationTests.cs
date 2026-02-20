using FluentAssertions;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Playback;

public sealed class PlaybackCoordinatorNavigationTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    static (AppData data, FakeAudioPlayer player, PlaybackCoordinator pc, Guid feedId)
        MakeSetup()
    {
        var data   = new AppData();
        var player = new FakeAudioPlayer();
        var pc     = new PlaybackCoordinator(data, player, () => Task.CompletedTask, new MemoryLogSink());
        var feedId = Guid.NewGuid();
        data.Feeds.Add(new Feed { Id = feedId, Title = "Test Feed" });
        return (data, player, pc, feedId);
    }

    static Episode MakeEpisode(Guid feedId, int daysAgo, bool markedPlayed = false) => new()
    {
        FeedId  = feedId,
        Title   = $"Episode -{daysAgo}d",
        AudioUrl = $"https://example.com/{daysAgo}.mp3",
        PubDate = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        ManuallyMarkedPlayed = markedPlayed,
    };

    // ── TryAdvanceToNext ─────────────────────────────────────────────────────

    [Fact]
    public void TryAdvanceToNext_no_current_returns_false()
    {
        var (_, _, pc, _) = MakeSetup();

        var found = pc.TryAdvanceToNext(out var next);

        found.Should().BeFalse();
        next.Should().BeNull();
    }

    [Fact]
    public void TryAdvanceToNext_returns_first_valid_episode_from_queue()
    {
        var (data, _, pc, feedId) = MakeSetup();
        var current = MakeEpisode(feedId, 2);
        var queued  = MakeEpisode(feedId, 3);
        data.Episodes.AddRange([current, queued]);
        data.Queue.Add(queued.Id);
        pc.Play(current);

        var found = pc.TryAdvanceToNext(out var next);

        found.Should().BeTrue();
        next!.Id.Should().Be(queued.Id);
        data.Queue.Should().BeEmpty(); // queue entry consumed
    }

    [Fact]
    public void TryAdvanceToNext_skips_invalid_queue_ids_and_falls_through_to_feed()
    {
        var (data, _, pc, feedId) = MakeSetup();
        // ep1=newest, ep2=current, ep3=oldest
        var ep1 = MakeEpisode(feedId, 1);
        var ep2 = MakeEpisode(feedId, 2);
        var ep3 = MakeEpisode(feedId, 3);
        data.Episodes.AddRange([ep1, ep2, ep3]);
        data.Queue.Add(Guid.NewGuid()); // ID that doesn't exist in Episodes
        pc.Play(ep2);

        var found = pc.TryAdvanceToNext(out var next);

        found.Should().BeTrue();
        next!.Id.Should().Be(ep3.Id); // fell through to same-feed (older episode)
    }

    [Fact]
    public void TryAdvanceToNext_falls_through_to_older_feed_episode_when_queue_empty()
    {
        var (data, _, pc, feedId) = MakeSetup();
        var ep1 = MakeEpisode(feedId, 1); // newest
        var ep2 = MakeEpisode(feedId, 2);
        var ep3 = MakeEpisode(feedId, 3); // oldest
        data.Episodes.AddRange([ep1, ep2, ep3]);
        pc.Play(ep2); // current = ep2 (middle)

        var found = pc.TryAdvanceToNext(out var next);

        found.Should().BeTrue();
        next!.Id.Should().Be(ep3.Id); // ep3 is older → next in feed
    }

    [Fact]
    public void TryAdvanceToNext_respects_UnplayedOnly_filter()
    {
        var (data, _, pc, feedId) = MakeSetup();
        var ep1    = MakeEpisode(feedId, 1);
        var ep2    = MakeEpisode(feedId, 2);
        var ep3    = MakeEpisode(feedId, 3, markedPlayed: true); // skipped
        var ep4    = MakeEpisode(feedId, 4); // should be returned
        data.Episodes.AddRange([ep1, ep2, ep3, ep4]);
        data.UnplayedOnly = true;
        pc.Play(ep2);

        var found = pc.TryAdvanceToNext(out var next);

        found.Should().BeTrue();
        next!.Id.Should().Be(ep4.Id);
    }

    [Fact]
    public void TryAdvanceToNext_wraps_to_newest_when_WrapAdvance_enabled()
    {
        var (data, _, pc, feedId) = MakeSetup();
        var ep1 = MakeEpisode(feedId, 1); // newest
        var ep2 = MakeEpisode(feedId, 2); // oldest (we're playing this one)
        data.Episodes.AddRange([ep1, ep2]);
        data.WrapAdvance = true;
        pc.Play(ep2); // at end of feed

        var found = pc.TryAdvanceToNext(out var next);

        found.Should().BeTrue();
        next!.Id.Should().Be(ep1.Id); // wraps to ep1 (newest)
    }

    [Fact]
    public void TryAdvanceToNext_returns_false_at_end_when_wrap_disabled()
    {
        var (data, _, pc, feedId) = MakeSetup();
        var ep1 = MakeEpisode(feedId, 1);
        var ep2 = MakeEpisode(feedId, 2); // oldest
        data.Episodes.AddRange([ep1, ep2]);
        data.WrapAdvance = false;
        pc.Play(ep2);

        var found = pc.TryAdvanceToNext(out var next);

        found.Should().BeFalse();
        next.Should().BeNull();
    }

    // ── TryFindPrev ──────────────────────────────────────────────────────────

    [Fact]
    public void TryFindPrev_no_current_returns_false()
    {
        var (_, _, pc, _) = MakeSetup();

        var found = pc.TryFindPrev(out var prev);

        found.Should().BeFalse();
        prev.Should().BeNull();
    }

    [Fact]
    public void TryFindPrev_at_newest_returns_false()
    {
        var (data, _, pc, feedId) = MakeSetup();
        var ep1 = MakeEpisode(feedId, 1); // newest → idx 0 in sorted list
        var ep2 = MakeEpisode(feedId, 2);
        data.Episodes.AddRange([ep1, ep2]);
        pc.Play(ep1); // already at the newest

        var found = pc.TryFindPrev(out var prev);

        found.Should().BeFalse();
        prev.Should().BeNull();
    }

    [Fact]
    public void TryFindPrev_returns_newer_episode()
    {
        var (data, _, pc, feedId) = MakeSetup();
        var ep1 = MakeEpisode(feedId, 1); // newest
        var ep2 = MakeEpisode(feedId, 2); // middle — current
        var ep3 = MakeEpisode(feedId, 3); // oldest
        data.Episodes.AddRange([ep1, ep2, ep3]);
        pc.Play(ep2);

        var found = pc.TryFindPrev(out var prev);

        found.Should().BeTrue();
        prev!.Id.Should().Be(ep1.Id); // ep1 is newer (idx-1)
    }
}
