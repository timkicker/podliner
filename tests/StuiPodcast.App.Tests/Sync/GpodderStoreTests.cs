using FluentAssertions;
using StuiPodcast.Core.Sync;
using StuiPodcast.Infra.Sync;
using System.Text;
using Xunit;

namespace StuiPodcast.App.Tests.Sync;

public sealed class GpodderStoreTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "podliner-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var dir = TempDir();
        try
        {
            var store = new GpodderStore(dir);
            var cfg = store.Load();

            cfg.IsConfigured.Should().BeFalse();
            cfg.DeviceId.Should().NotBeNullOrEmpty();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Default_device_id_starts_with_podliner_and_is_max_64_chars()
    {
        var dir = TempDir();
        try
        {
            var store = new GpodderStore(dir);
            store.Load();

            store.Current.DeviceId.Should().StartWith("podliner-");
            store.Current.DeviceId.Length.Should().BeLessThanOrEqualTo(64);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Credentials_roundtrip_through_save_and_reload()
    {
        var dir = TempDir();
        try
        {
            var store = new GpodderStore(dir);
            store.Load();
            store.Current.ServerUrl = "https://gpodder.net";
            store.Current.Username  = "testuser";
            store.Current.Password  = "s3cr3t";
            store.Save();

            var store2 = new GpodderStore(dir);
            store2.Load();

            store2.Current.ServerUrl.Should().Be("https://gpodder.net");
            store2.Current.Username.Should().Be("testuser");
            store2.Current.Password.Should().Be("s3cr3t");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Timestamps_roundtrip_through_save_and_reload()
    {
        var dir = TempDir();
        try
        {
            var store = new GpodderStore(dir);
            store.Load();
            store.Current.SubsTimestamp    = 1234567890L;
            store.Current.ActionsTimestamp = 9876543210L;
            store.Save();

            var store2 = new GpodderStore(dir);
            store2.Load();

            store2.Current.SubsTimestamp.Should().Be(1234567890L);
            store2.Current.ActionsTimestamp.Should().Be(9876543210L);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PendingActions_survive_save_and_reload()
    {
        var dir = TempDir();
        try
        {
            var store = new GpodderStore(dir);
            store.Load();
            store.Current.PendingActions.Add(new PendingGpodderAction
            {
                PodcastUrl = "https://feed.com/rss",
                EpisodeUrl = "https://feed.com/ep.mp3",
                Action     = "play",
                Timestamp  = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero),
                Position   = 120,
                Total      = 600,
            });
            store.Save();

            var store2 = new GpodderStore(dir);
            store2.Load();

            store2.Current.PendingActions.Should().HaveCount(1);
            var a = store2.Current.PendingActions[0];
            a.PodcastUrl.Should().Be("https://feed.com/rss");
            a.EpisodeUrl.Should().Be("https://feed.com/ep.mp3");
            a.Action.Should().Be("play");
            a.Position.Should().Be(120);
            a.Total.Should().Be(600);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_tolerates_corrupt_json_and_returns_defaults()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.Combine(dir, "gpodder.json");
            File.WriteAllText(filePath, "not json", Encoding.UTF8);

            var store = new GpodderStore(dir);
            var act = () => store.Load();

            act.Should().NotThrow();
            store.Current.IsConfigured.Should().BeFalse();
            store.Current.DeviceId.Should().NotBeNullOrEmpty();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_removes_orphaned_tmp_file()
    {
        var dir = TempDir();
        try
        {
            var tmpPath = Path.Combine(dir, "gpodder.json.tmp");
            File.WriteAllText(tmpPath, "{}", Encoding.UTF8);

            var store = new GpodderStore(dir);
            store.Load();

            File.Exists(tmpPath).Should().BeFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
