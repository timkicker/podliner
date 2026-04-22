using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.Infra.Tests.Storage;

// Regression for the debounce-timer leak fix: LibraryStore now implements
// IDisposable and must flush any pending save before releasing the timer.
public sealed class LibraryStoreDisposeTests : IDisposable
{
    private readonly string _dir;

    public LibraryStoreDisposeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-libdispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var store = new LibraryStore(_dir);
        store.Load();

        store.Dispose();
        var act = () => store.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_flushes_pending_debounced_save()
    {
        var store = new LibraryStore(_dir);
        store.Load();

        // AddOrUpdateFeed triggers SaveAsync internally (debounced 2.5s).
        // Without Dispose flushing it, this would never hit disk if we exit immediately.
        var feed = new Feed { Title = "Test", Url = "https://example.com/feed.xml" };
        store.AddOrUpdateFeed(feed);

        store.Dispose();

        // Reload to verify.
        var reload = new LibraryStore(_dir);
        var lib = reload.Load();
        lib.Feeds.Should().HaveCount(1);
        lib.Feeds[0].Title.Should().Be("Test");
    }

    [Fact]
    public void Dispose_without_pending_save_does_not_write()
    {
        var store = new LibraryStore(_dir);
        store.Load();

        store.Dispose();
        File.Exists(store.FilePath).Should().BeFalse();
    }

    [Fact]
    public void Rapid_mutations_followed_by_Dispose_all_persist()
    {
        var store = new LibraryStore(_dir);
        store.Load();
        var feed = new Feed { Title = "F", Url = "https://x.com/f.xml" };
        store.AddOrUpdateFeed(feed);

        for (int i = 0; i < 10; i++)
        {
            var ep = new Episode { FeedId = feed.Id, Title = $"ep-{i}", AudioUrl = $"https://x.com/{i}.mp3" };
            store.AddOrUpdateEpisode(ep);
        }

        // Everything debounced — Dispose must flush.
        store.Dispose();

        var reload = new LibraryStore(_dir);
        var lib = reload.Load();
        lib.Episodes.Should().HaveCount(10);
    }
}
