using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdSystemModuleTests
{
    private readonly FakeUiShell _ui = new();

    // ── ExecOsd ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExecOsd_shows_joined_text()
    {
        CmdSystemModule.ExecOsd(new[] { "Hello", "World" }, _ui);
        _ui.OsdMessages.Should().Contain(m => m.Text == "Hello World");
    }

    [Fact]
    public void ExecOsd_empty_args_shows_usage()
    {
        CmdSystemModule.ExecOsd(Array.Empty<string>(), _ui);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage: :osd"));
    }

    [Fact]
    public void ExecOsd_whitespace_only_shows_usage()
    {
        CmdSystemModule.ExecOsd(new[] { "   " }, _ui);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage: :osd"));
    }

    // ── ExecLogs ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExecLogs_default_tail_is_500()
    {
        CmdSystemModule.ExecLogs(Array.Empty<string>(), _ui);
        _ui.LastLogsOverlayTail.Should().Be(500);
    }

    [Fact]
    public void ExecLogs_parses_tail_from_arg()
    {
        CmdSystemModule.ExecLogs(new[] { "1000" }, _ui);
        _ui.LastLogsOverlayTail.Should().Be(1000);
    }

    [Fact]
    public void ExecLogs_clamps_huge_tail_to_5000()
    {
        CmdSystemModule.ExecLogs(new[] { "99999" }, _ui);
        _ui.LastLogsOverlayTail.Should().Be(5000);
    }

    [Fact]
    public void ExecLogs_invalid_arg_uses_default()
    {
        CmdSystemModule.ExecLogs(new[] { "banana" }, _ui);
        _ui.LastLogsOverlayTail.Should().Be(500);
    }

    [Fact]
    public void ExecLogs_negative_arg_uses_default()
    {
        CmdSystemModule.ExecLogs(new[] { "-100" }, _ui);
        _ui.LastLogsOverlayTail.Should().Be(500);
    }

    // ── ExecWrite ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecWrite_dispatches_save_asynchronously()
    {
        int saveCalls = 0;
        Func<Task> persist = () => { saveCalls++; return Task.CompletedTask; };

        CmdSystemModule.ExecWrite(persist, _ui);

        // Fire-and-forget: wait briefly for the Task.Run to execute.
        for (int i = 0; i < 20 && saveCalls == 0; i++) await Task.Delay(25);

        saveCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecWrite_does_not_block_caller()
    {
        Func<Task> slowPersist = async () => { await Task.Delay(500); };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        CmdSystemModule.ExecWrite(slowPersist, _ui);
        sw.Stop();

        // Should return immediately (fire-and-forget).
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200));
    }
}
