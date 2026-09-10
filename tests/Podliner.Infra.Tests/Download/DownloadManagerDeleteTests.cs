using FluentAssertions;
using Podliner.Core;
using Podliner.Infra.Download;
using Podliner.Infra.Storage;
using Xunit;

namespace Podliner.Infra.Tests.Download;

// A podcast client that can download but never delete fills the disk and
// leaves the user no way out. ":download" on a finished download used to call
// Forget(), which drops the map entry and says "Download unqueued" while the
// file stays exactly where it was.
public sealed class DownloadManagerDeleteTests : IDisposable
{
    private readonly string _dir;

    public DownloadManagerDeleteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-dldel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (DownloadManager mgr, AppData data, LibraryStore lib, Guid id, string path) Downloaded(int bytes = 2048)
    {
        var data = new AppData();
        var lib = new LibraryStore(_dir); lib.Load();
        var mgr = new DownloadManager(data, lib, _dir);

        var id = Guid.NewGuid();
        var path = Path.Combine(_dir, "episode.mp3");
        File.WriteAllBytes(path, new byte[bytes]);

        data.DownloadMap[id] = new DownloadStatus
        {
            State = DownloadState.Done,
            LocalPath = path,
            BytesReceived = bytes,
            TotalBytes = bytes
        };

        return (mgr, data, lib, id, path);
    }

    [Fact]
    public void Deleting_a_finished_download_removes_the_file()
    {
        var (mgr, data, lib, id, path) = Downloaded(2048);
        using (mgr) using (lib)
        {
            var freed = mgr.DeleteLocalFile(id);

            freed.Should().Be(2048);
            File.Exists(path).Should().BeFalse();
            data.DownloadMap.Should().NotContainKey(id, "the episode is no longer downloaded");
        }
    }

    [Fact]
    public void Deleting_reports_zero_when_there_is_nothing_to_delete()
    {
        var data = new AppData();
        using var lib = new LibraryStore(_dir); lib.Load();
        using var mgr = new DownloadManager(data, lib, _dir);

        mgr.DeleteLocalFile(Guid.NewGuid()).Should().Be(0);
    }

    [Fact]
    public void Deleting_an_entry_whose_file_is_already_gone_still_clears_the_state()
    {
        var (mgr, data, lib, id, path) = Downloaded();
        using (mgr) using (lib)
        {
            File.Delete(path);

            mgr.DeleteLocalFile(id).Should().Be(0, "no bytes were freed, the file was gone");
            data.DownloadMap.Should().NotContainKey(id, "a stale entry has to go too");
        }
    }

    [Fact]
    public void A_deleted_download_does_not_come_back_from_the_index()
    {
        var (mgr, data, lib, id, path) = Downloaded();
        using (mgr) using (lib)
        {
            mgr.DeleteLocalFile(id);
            mgr.SaveIndexNow();
        }

        var data2 = new AppData();
        using var lib2 = new LibraryStore(_dir); lib2.Load();
        using var mgr2 = new DownloadManager(data2, lib2, _dir);

        data2.DownloadMap.Should().NotContainKey(id);
    }

    [Fact]
    public void The_state_reported_after_a_delete_is_None()
    {
        var (mgr, data, lib, id, _) = Downloaded();
        using (mgr) using (lib)
        {
            mgr.DeleteLocalFile(id);

            mgr.GetState(id).Should().Be(DownloadState.None);
        }
    }
}
