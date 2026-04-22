using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class FeedStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly LibraryStore _lib;
    private readonly FeedStore _sut;

    public FeedStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-feedstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lib = new LibraryStore(_dir);
        _lib.Load();
        _sut = new FeedStore(_lib);
    }

    public void Dispose()
    {
        try { _lib.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_store_has_zero_count_and_empty_snapshot()
    {
        _sut.Count.Should().Be(0);
        _sut.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void TryGet_unknown_returns_false()
    {
        _sut.TryGet(Guid.NewGuid(), out var f).Should().BeFalse();
        f.Should().BeNull();
    }

    [Fact]
    public void FindByUrl_unknown_returns_null()
    {
        _sut.FindByUrl("https://nope").Should().BeNull();
    }

    [Fact]
    public void FindByUrl_is_case_insensitive()
    {
        _sut.AddOrUpdate(new Feed { Title = "A", Url = "https://Example.com/RSS" });
        _sut.FindByUrl("https://example.com/rss").Should().NotBeNull();
    }

    [Fact]
    public void Snapshot_is_stable_across_mutations()
    {
        _sut.AddOrUpdate(new Feed { Title = "A", Url = "https://a.com" });
        var snap = _sut.Snapshot();
        _sut.AddOrUpdate(new Feed { Title = "B", Url = "https://b.com" });

        snap.Should().HaveCount(1, "snapshot must be a copy, not live view");
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddOrUpdate_new_feed_assigns_id_and_stores()
    {
        var f = _sut.AddOrUpdate(new Feed { Title = "A", Url = "https://a.com" });
        f.Id.Should().NotBe(Guid.Empty);
        _sut.Find(f.Id).Should().NotBeNull();
    }

    [Fact]
    public void AddOrUpdate_existing_id_updates_metadata()
    {
        var f = _sut.AddOrUpdate(new Feed { Title = "old", Url = "https://a.com" });
        f.Title = "new";
        _sut.AddOrUpdate(f);

        _sut.Find(f.Id)!.Title.Should().Be("new");
        _sut.Count.Should().Be(1);
    }

    [Fact]
    public void Remove_drops_feed()
    {
        var f = _sut.AddOrUpdate(new Feed { Title = "A", Url = "https://a.com" });
        _sut.Remove(f.Id).Should().BeTrue();
        _sut.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_unknown_returns_false()
    {
        _sut.Remove(Guid.NewGuid()).Should().BeFalse();
    }

    // ── Events ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddOrUpdate_fires_Added_then_Updated()
    {
        var kinds = new List<FeedChangeKind>();
        _sut.Changed += c => kinds.Add(c.Kind);

        var f = _sut.AddOrUpdate(new Feed { Title = "A", Url = "https://a.com" });
        f.Title = "B";
        _sut.AddOrUpdate(f);

        kinds.Should().ContainInOrder(FeedChangeKind.Added, FeedChangeKind.Updated);
    }

    [Fact]
    public void Remove_fires_Removed()
    {
        var f = _sut.AddOrUpdate(new Feed { Title = "A", Url = "https://a.com" });
        FeedChangeKind? seen = null;
        _sut.Changed += c => { if (c.Kind == FeedChangeKind.Removed) seen = c.Kind; };

        _sut.Remove(f.Id);
        seen.Should().Be(FeedChangeKind.Removed);
    }

    // ── URL-index invalidation ──────────────────────────────────────────────

    [Fact]
    public void URL_index_is_refreshed_after_mutation()
    {
        var f = _sut.AddOrUpdate(new Feed { Title = "A", Url = "https://old.com" });
        _sut.FindByUrl("https://old.com").Should().NotBeNull();

        // Mutate URL.
        f.Url = "https://new.com";
        _sut.AddOrUpdate(f);

        _sut.FindByUrl("https://old.com").Should().BeNull();
        _sut.FindByUrl("https://new.com").Should().NotBeNull();
    }

    // ── Consistency with LibraryStore ───────────────────────────────────────

    [Fact]
    public void Reads_reflect_direct_LibraryStore_mutations()
    {
        var f = new Feed { Title = "viaLib", Url = "https://lib.com" };
        _lib.AddOrUpdateFeed(f);

        _sut.Find(f.Id).Should().NotBeNull();
        _sut.FindByUrl("https://lib.com").Should().NotBeNull();
    }
}
