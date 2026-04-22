using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdDownloadsModuleTests : IDisposable
{
    private readonly string _dir;
    private readonly AppData _data;
    private readonly DownloadManager _dlm;
    private readonly FakeUiShell _ui;
    private bool _saved;

    public CmdDownloadsModuleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-cmddl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _data = new AppData();
        _dlm = new DownloadManager(_data, _dir);
        _ui = new FakeUiShell();
    }

    public void Dispose()
    {
        try { _dlm.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task Save() { _saved = true; return Task.CompletedTask; }

    private Episode MakeEpisode(DownloadState? state = null)
    {
        var ep = new Episode { Title = "Test", AudioUrl = "https://x.com/e.mp3" };
        _data.Episodes.Add(ep);
        _ui.SelectedEpisode = ep;
        if (state.HasValue)
            _data.DownloadMap[ep.Id] = new DownloadStatus { State = state.Value };
        return ep;
    }

    // ── :downloads (status) ──────────────────────────────────────────────────

    [Fact]
    public void Downloads_status_shows_queue_running_failed_counts()
    {
        _data.DownloadQueue.Add(Guid.NewGuid());
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Running };
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Running };
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Failed };

        CmdDownloadsModule.HandleDownloads(":downloads", _ui, _data, _dlm, Save);

        _ui.OsdMessages.Should().Contain(m =>
            m.Text.Contains("queue 1") && m.Text.Contains("running 2") && m.Text.Contains("failed 1"));
    }

    // ── :downloads retry-failed ──────────────────────────────────────────────

    [Fact]
    public void Retry_failed_enqueues_all_failed_downloads()
    {
        var f1 = Guid.NewGuid();
        var f2 = Guid.NewGuid();
        _data.DownloadMap[f1] = new DownloadStatus { State = DownloadState.Failed };
        _data.DownloadMap[f2] = new DownloadStatus { State = DownloadState.Failed };
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Done };

        CmdDownloadsModule.HandleDownloads(":downloads retry-failed", _ui, _data, _dlm, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("retried 2"));
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Retry_failed_with_none_reports_zero()
    {
        _data.DownloadMap[Guid.NewGuid()] = new DownloadStatus { State = DownloadState.Done };

        CmdDownloadsModule.HandleDownloads(":downloads retry-failed", _ui, _data, _dlm, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("retried 0"));
    }

    // ── :downloads clear-queue ───────────────────────────────────────────────

    [Fact]
    public void Clear_queue_empties_queue()
    {
        _data.DownloadQueue.AddRange(new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() });

        CmdDownloadsModule.HandleDownloads(":downloads clear-queue", _ui, _data, _dlm, Save);

        _data.DownloadQueue.Should().BeEmpty();
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("cleared queue (3)"));
    }

    [Fact]
    public void Clear_queue_on_empty_reports_zero()
    {
        CmdDownloadsModule.HandleDownloads(":downloads clear-queue", _ui, _data, _dlm, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("cleared queue (0)"));
    }

    // ── :downloads unknown subcommand ────────────────────────────────────────

    [Fact]
    public void Unknown_subcommand_shows_usage()
    {
        CmdDownloadsModule.HandleDownloads(":downloads bogus", _ui, _data, _dlm, Save);

        _ui.OsdMessages.Should().Contain(m =>
            m.Text.Contains("retry-failed") && m.Text.Contains("clear-queue") && m.Text.Contains("open-dir"));
    }

    // ── :dl cancel ───────────────────────────────────────────────────────────

    [Fact]
    public void Dl_cancel_forgets_download_state()
    {
        var ep = MakeEpisode(DownloadState.Done);
        _data.DownloadQueue.Add(ep.Id);

        CmdDownloadsModule.HandleDownloads(":dl cancel", _ui, _data, _dlm, Save);

        _data.DownloadMap.ContainsKey(ep.Id).Should().BeFalse();
        _data.DownloadQueue.Should().NotContain(ep.Id);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("canceled"));
    }

    // ── :dl (toggle semantics) ───────────────────────────────────────────────

    [Fact]
    public void Dl_default_enqueues_when_state_is_None()
    {
        var ep = MakeEpisode();

        CmdDownloadsModule.HandleDownloads(":dl", _ui, _data, _dlm, Save);

        _dlm.GetState(ep.Id).Should().BeOneOf(DownloadState.Queued, DownloadState.Running, DownloadState.Failed);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("queued"));
    }

    [Fact]
    public void Dl_default_enqueues_when_state_is_Failed()
    {
        var ep = MakeEpisode(DownloadState.Failed);

        CmdDownloadsModule.HandleDownloads(":dl", _ui, _data, _dlm, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("queued"));
    }

    [Fact]
    public void Dl_default_enqueues_when_state_is_Canceled()
    {
        var ep = MakeEpisode(DownloadState.Canceled);

        CmdDownloadsModule.HandleDownloads(":dl", _ui, _data, _dlm, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("queued"));
    }

    // ── :dl with no selected episode ─────────────────────────────────────────

    [Fact]
    public void Dl_with_no_selection_is_noop_and_returns_handled()
    {
        _ui.SelectedEpisode = null;

        var handled = CmdDownloadsModule.HandleDownloads(":dl", _ui, _data, _dlm, Save);

        handled.Should().BeTrue();
        _ui.OsdMessages.Should().BeEmpty();
    }

    // ── Unrelated commands return false ──────────────────────────────────────

    [Fact]
    public void Unrelated_command_returns_false()
    {
        CmdDownloadsModule.HandleDownloads(":help", _ui, _data, _dlm, Save).Should().BeFalse();
        CmdDownloadsModule.HandleDownloads(":toggle", _ui, _data, _dlm, Save).Should().BeFalse();
    }

    // ── DlToggle helper ──────────────────────────────────────────────────────

    [Fact]
    public void DlToggle_off_forgets_state()
    {
        var ep = MakeEpisode(DownloadState.Done);
        _data.DownloadQueue.Add(ep.Id);

        CmdDownloadsModule.DlToggle("off", _ui, _data, Save, _dlm);

        _data.DownloadMap.ContainsKey(ep.Id).Should().BeFalse();
        _data.DownloadQueue.Should().NotContain(ep.Id);
    }

    [Fact]
    public void DlToggle_on_enqueues()
    {
        var ep = MakeEpisode();

        CmdDownloadsModule.DlToggle("on", _ui, _data, Save, _dlm);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("queued"));
    }

    [Fact]
    public void DlToggle_with_no_selection_is_noop()
    {
        _ui.SelectedEpisode = null;

        CmdDownloadsModule.DlToggle("on", _ui, _data, Save, _dlm);

        _ui.OsdMessages.Should().BeEmpty();
    }
}
