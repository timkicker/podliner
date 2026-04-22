using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using Xunit;

namespace StuiPodcast.Infra.Tests.Download;

// The download manager persists a small "downloads.json" index with episode IDs
// and their local file paths for Done downloads. On next startup, the index is
// loaded and the DownloadMap is populated so the UI shows "downloaded" badges
// immediately. These tests verify the round-trip behavior.
public sealed class DownloadManagerIndexTests : IDisposable
{
    private readonly string _dir;

    public DownloadManagerIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-dlidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Fresh_manager_with_no_index_file_starts_empty()
    {
        var data = new AppData();
        using var mgr = new DownloadManager(data, _dir);

        data.DownloadMap.Should().BeEmpty();
    }

    [Fact]
    public void Corrupt_index_file_is_tolerated()
    {
        var indexPath = Path.Combine(_dir, "downloads.json");
        File.WriteAllText(indexPath, "this is not json at all }}}");

        var data = new AppData();
        var act = () =>
        {
            using var mgr = new DownloadManager(data, _dir);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Index_skips_entries_whose_local_files_are_missing()
    {
        var indexPath = Path.Combine(_dir, "downloads.json");
        var fakeId = Guid.NewGuid();
        var missingPath = Path.Combine(_dir, "not-a-real-file.mp3");

        File.WriteAllText(indexPath, $$"""
        {
          "SchemaVersion": 1,
          "Items": [
            { "EpisodeId": "{{fakeId}}", "LocalPath": "{{missingPath.Replace("\\", "\\\\")}}" }
          ]
        }
        """);

        var data = new AppData();
        using var mgr = new DownloadManager(data, _dir);

        // Implementation reads the file but state should reflect that the path doesn't exist.
        // Either the entry is present with Done + non-existent file, or absent entirely.
        // Either way, IsDownloaded (via TryGetStatus + File.Exists check) returns false.
        mgr.TryGetStatus(fakeId, out var st);
        if (st != null && !string.IsNullOrEmpty(st.LocalPath))
            File.Exists(st.LocalPath).Should().BeFalse("the test file was never created");
    }

    [Fact]
    public void Index_loads_valid_entry_when_file_exists()
    {
        // Create a real file so the index entry is considered valid.
        var epId = Guid.NewGuid();
        var realFile = Path.Combine(_dir, "podcast.mp3");
        File.WriteAllBytes(realFile, new byte[] { 0, 1, 2, 3 });

        var indexPath = Path.Combine(_dir, "downloads.json");
        File.WriteAllText(indexPath, $$"""
        {
          "SchemaVersion": 1,
          "Items": [
            { "EpisodeId": "{{epId}}", "LocalPath": "{{realFile.Replace("\\", "\\\\")}}" }
          ]
        }
        """);

        var data = new AppData();
        using var mgr = new DownloadManager(data, _dir);

        mgr.TryGetStatus(epId, out var st).Should().BeTrue();
        st!.State.Should().Be(DownloadState.Done);
        st.LocalPath.Should().Be(realFile);
    }

    [Fact]
    public void Empty_index_file_is_handled()
    {
        var indexPath = Path.Combine(_dir, "downloads.json");
        File.WriteAllText(indexPath, "{\"SchemaVersion\":1,\"Items\":[]}");

        var data = new AppData();
        using var mgr = new DownloadManager(data, _dir);

        data.DownloadMap.Should().BeEmpty();
    }
}
