using FluentAssertions;
using StuiPodcast.Infra.Storage;
using StuiPodcast.Core;
using System.Text;
using Xunit;

namespace StuiPodcast.Infra.Tests.Storage;

public sealed class ConfigStoreTests
{
    [Fact]
    public void Load_creates_defaults_when_missing_and_save_writes_json()
    {
        var dir = CreateTempDir();
        var store = new ConfigStore(dir);

        var cfg = store.Load();
        cfg.EnginePreference.Should().NotBeNullOrWhiteSpace();

        store.SaveNow();

        File.Exists(store.FilePath).Should().BeTrue();
        var json = File.ReadAllText(store.FilePath, Encoding.UTF8);
        json.Should().Contain("EnginePreference");
    }

    [Fact]
    public void Load_tolerates_comments_and_trailing_commas()
    {
        var dir = CreateTempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "appsettings.json");

        File.WriteAllText(path, """
{
  // comment
  "EnginePreference": "mpv",
  "Volume0_100": 10,
}
""", Encoding.UTF8);

        var store = new ConfigStore(dir);
        var cfg = store.Load();

        cfg.EnginePreference.Should().Be("mpv");
        cfg.Volume0_100.Should().Be(10);
    }

    static string CreateTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "podliner-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }
}

public sealed class LibraryStoreTests
{
    [Fact]
    public void Load_creates_empty_library_and_save_persists()
    {
        var dir = CreateTempDir();
        var store = new LibraryStore(dir);

        var lib = store.Load();
        lib.Feeds.Should().NotBeNull();
        lib.Episodes.Should().NotBeNull();

        var feed = new Feed { Title = "T", Url = "https://example.com/feed" };
        var ep = new Episode { FeedId = feed.Id, Title = "E", AudioUrl = "https://example.com/audio.mp3" };

        store.Current.Feeds.Add(feed);
        store.Current.Episodes.Add(ep);

        store.SaveNow();

        File.Exists(store.FilePath).Should().BeTrue();
        var json = File.ReadAllText(store.FilePath, Encoding.UTF8);

        json.Should().Contain("https://example.com/feed");
        json.Should().Contain("https://example.com/audio.mp3");
    }

    static string CreateTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "podliner-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }
}
