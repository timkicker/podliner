using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class EpisodeStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly LibraryStore _lib;
    private readonly EpisodeStore _sut;
    private readonly Guid _feedId;

    public EpisodeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-epstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lib = new LibraryStore(_dir);
        _lib.Load();

        _feedId = Guid.NewGuid();
        _lib.AddOrUpdateFeed(new Feed { Id = _feedId, Title = "F", Url = "https://x.com/feed" });

        _sut = new EpisodeStore(_lib);
    }

    public void Dispose()
    {
        try { _lib.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    Episode MakeEpisode(string title = "ep") =>
        new Episode { FeedId = _feedId, Title = title, AudioUrl = $"https://x.com/{Guid.NewGuid():N}.mp3" };

    // ── Reads ────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_store_reports_zero_count()
    {
        _sut.Count.Should().Be(0);
        _sut.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void TryGet_unknown_returns_false()
    {
        _sut.TryGet(Guid.NewGuid(), out var ep).Should().BeFalse();
        ep.Should().BeNull();
    }

    [Fact]
    public void AddOrUpdate_new_episode_goes_through_snapshot()
    {
        var ep = _sut.AddOrUpdate(MakeEpisode("A"));

        _sut.Count.Should().Be(1);
        _sut.Snapshot().Should().ContainSingle(e => e.Id == ep.Id);
        _sut.TryGet(ep.Id, out var got).Should().BeTrue();
        got!.Title.Should().Be("A");
    }

    [Fact]
    public void Find_returns_null_for_unknown()
    {
        _sut.Find(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Snapshot_is_immutable_copy()
    {
        _sut.AddOrUpdate(MakeEpisode("A"));
        var snap = _sut.Snapshot();
        _sut.AddOrUpdate(MakeEpisode("B"));

        // Snapshot taken before the second add must still have only one entry.
        snap.Should().HaveCount(1, "Snapshot must be a stable copy, not a live view");
    }

    [Fact]
    public void WhereByFeed_filters_to_feed()
    {
        var otherFeed = Guid.NewGuid();
        _lib.AddOrUpdateFeed(new Feed { Id = otherFeed, Title = "Other", Url = "https://y.com" });

        _sut.AddOrUpdate(MakeEpisode("A"));
        _sut.AddOrUpdate(new Episode { FeedId = otherFeed, Title = "B", AudioUrl = "https://y.com/b" });
        _sut.AddOrUpdate(MakeEpisode("C"));

        var result = _sut.WhereByFeed(_feedId);
        result.Should().HaveCount(2);
        result.Select(e => e.Title).Should().BeEquivalentTo(new[] { "A", "C" });
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddOrUpdate_existing_updates_not_duplicates()
    {
        var ep = _sut.AddOrUpdate(MakeEpisode("original"));
        ep.Title = "changed";
        _sut.AddOrUpdate(ep);

        _sut.Count.Should().Be(1);
        _sut.Find(ep.Id)!.Title.Should().Be("changed");
    }

    [Fact]
    public void Remove_drops_entry()
    {
        var ep = _sut.AddOrUpdate(MakeEpisode("A"));
        _sut.Remove(ep.Id).Should().BeTrue();

        _sut.Count.Should().Be(0);
        _sut.Find(ep.Id).Should().BeNull();
    }

    [Fact]
    public void Remove_unknown_returns_false()
    {
        _sut.Remove(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void RemoveByFeed_drops_all_for_feed()
    {
        var other = Guid.NewGuid();
        _lib.AddOrUpdateFeed(new Feed { Id = other, Title = "Other", Url = "https://y.com" });

        _sut.AddOrUpdate(MakeEpisode("A"));
        _sut.AddOrUpdate(MakeEpisode("B"));
        var remaining = _sut.AddOrUpdate(new Episode { FeedId = other, Title = "keep", AudioUrl = "https://y.com/keep" });

        _sut.RemoveByFeed(_feedId);

        _sut.Count.Should().Be(1);
        _sut.Find(remaining.Id).Should().NotBeNull();
    }

    // ── Events ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddOrUpdate_fires_Added_then_Updated()
    {
        var kinds = new List<EpisodeChangeKind>();
        _sut.Changed += c => kinds.Add(c.Kind);

        var ep = _sut.AddOrUpdate(MakeEpisode("A"));
        ep.Title = "B";
        _sut.AddOrUpdate(ep);

        kinds.Should().ContainInOrder(EpisodeChangeKind.Added, EpisodeChangeKind.Updated);
    }

    [Fact]
    public void Remove_fires_Removed()
    {
        var ep = _sut.AddOrUpdate(MakeEpisode("A"));
        EpisodeChangeKind? seen = null;
        _sut.Changed += c => { if (c.Kind == EpisodeChangeKind.Removed) seen = c.Kind; };

        _sut.Remove(ep.Id);
        seen.Should().Be(EpisodeChangeKind.Removed);
    }

    [Fact]
    public void RemoveByFeed_fires_one_event_per_removed_episode()
    {
        var a = _sut.AddOrUpdate(MakeEpisode("A"));
        var b = _sut.AddOrUpdate(MakeEpisode("B"));

        int removedEvents = 0;
        _sut.Changed += c => { if (c.Kind == EpisodeChangeKind.Removed) removedEvents++; };

        _sut.RemoveByFeed(_feedId);
        removedEvents.Should().Be(2);
    }

    [Fact]
    public void SetProgress_updates_episode_and_fires_Updated()
    {
        var ep = _sut.AddOrUpdate(new Episode { FeedId = _feedId, Title = "A", AudioUrl = "https://x.com/a.mp3", DurationMs = 100_000 });
        bool fired = false;
        _sut.Changed += c => { if (c.Kind == EpisodeChangeKind.Updated) fired = true; };

        _sut.SetProgress(ep.Id, 5_000, DateTimeOffset.UtcNow);

        _sut.Find(ep.Id)!.Progress.LastPosMs.Should().Be(5_000);
        fired.Should().BeTrue();
    }

    [Fact]
    public void SetSaved_toggles_flag()
    {
        var ep = _sut.AddOrUpdate(MakeEpisode("A"));
        _sut.SetSaved(ep.Id, true);
        _sut.Find(ep.Id)!.Saved.Should().BeTrue();
        _sut.SetSaved(ep.Id, false);
        _sut.Find(ep.Id)!.Saved.Should().BeFalse();
    }

    [Fact]
    public void SetManuallyMarkedPlayed_toggles_flag()
    {
        var ep = _sut.AddOrUpdate(MakeEpisode("A"));
        _sut.SetManuallyMarkedPlayed(ep.Id, true);
        _sut.Find(ep.Id)!.ManuallyMarkedPlayed.Should().BeTrue();
    }

    // ── Construction from existing library ──────────────────────────────────

    [Fact]
    public void Construction_seeds_from_LibraryStore_contents()
    {
        var ep = new Episode { FeedId = _feedId, Title = "pre-existing", AudioUrl = "https://x.com/pre.mp3" };
        _lib.AddOrUpdateEpisode(ep);

        var fresh = new EpisodeStore(_lib);
        fresh.Count.Should().Be(1);
        fresh.Find(ep.Id)!.Title.Should().Be("pre-existing");
    }

    // ── Consistency with LibraryStore (migration safety net) ────────────────

    [Fact]
    public void EpisodeStore_and_LibraryStore_see_same_entries_after_AddOrUpdate()
    {
        // During the migration, some code paths still write via
        // _app.AddOrUpdateEpisode (LibraryStore). EpisodeStore must observe
        // those mutations immediately because it delegates reads to the
        // same LibraryStore instance.
        var ep = new Episode { FeedId = _feedId, Title = "viaLib", AudioUrl = "https://x.com/lib.mp3" };
        _lib.AddOrUpdateEpisode(ep);

        _sut.Find(ep.Id).Should().NotBeNull();
        _sut.TryGet(ep.Id, out var found).Should().BeTrue();
        found!.Title.Should().Be("viaLib");
    }

    [Fact]
    public void EpisodeStore_reflects_SetSaved_through_LibraryStore()
    {
        // Symmetric: any direct LibraryStore mutation should be visible.
        var ep = _sut.AddOrUpdate(new Episode { FeedId = _feedId, Title = "A", AudioUrl = "https://x.com/a.mp3" });
        _lib.SetSaved(ep.Id, true);

        _sut.Find(ep.Id)!.Saved.Should().BeTrue();
    }

    // ── Concurrency smoke ────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_reads_and_writes_do_not_throw()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                var id = ids[i % ids.Length];
                _sut.AddOrUpdate(new Episode { Id = id, FeedId = _feedId, Title = $"e{i}", AudioUrl = $"https://x.com/{i}.mp3" });
                i++;
            }
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _sut.Snapshot();
                _sut.Find(ids[0]);
                _sut.WhereByFeed(_feedId);
            }
        });

        var act = async () => await Task.WhenAll(writer, reader);
        await act.Should().NotThrowAsync();
    }
}
