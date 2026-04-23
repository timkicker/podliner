using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdSystemModuleTests
{
    private readonly FakeUiShell _ui = new();

    static SystemUseCase Make(FakeUiShell ui, Func<Task>? persist = null)
        => new(ui, persist ?? (() => Task.CompletedTask));

    // ── ExecOsd ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExecOsd_shows_joined_text()
    {
        Make(_ui).ExecOsd(new[] { "Hello", "World" });
        _ui.OsdMessages.Should().Contain(m => m.Text == "Hello World");
    }

    [Fact]
    public void ExecOsd_empty_args_shows_usage()
    {
        Make(_ui).ExecOsd(Array.Empty<string>());
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage: :osd"));
    }

    [Fact]
    public void ExecOsd_whitespace_only_shows_usage()
    {
        Make(_ui).ExecOsd(new[] { "   " });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage: :osd"));
    }

    // ── ExecLogs ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExecLogs_default_tail_is_500()
    {
        Make(_ui).ExecLogs(Array.Empty<string>());
        _ui.LastLogsOverlayTail.Should().Be(500);
    }

    [Fact]
    public void ExecLogs_parses_tail_from_arg()
    {
        Make(_ui).ExecLogs(new[] { "1000" });
        _ui.LastLogsOverlayTail.Should().Be(1000);
    }

    [Fact]
    public void ExecLogs_clamps_huge_tail_to_5000()
    {
        Make(_ui).ExecLogs(new[] { "99999" });
        _ui.LastLogsOverlayTail.Should().Be(5000);
    }

    [Fact]
    public void ExecLogs_invalid_arg_uses_default()
    {
        Make(_ui).ExecLogs(new[] { "banana" });
        _ui.LastLogsOverlayTail.Should().Be(500);
    }

    [Fact]
    public void ExecLogs_negative_arg_uses_default()
    {
        Make(_ui).ExecLogs(new[] { "-100" });
        _ui.LastLogsOverlayTail.Should().Be(500);
    }

    // ── ExecWrite ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecWrite_dispatches_save_asynchronously()
    {
        int saveCalls = 0;
        Func<Task> persist = () => { saveCalls++; return Task.CompletedTask; };

        Make(_ui, persist).ExecWrite();

        // Fire-and-forget: wait briefly for the Task.Run to execute.
        for (int i = 0; i < 20 && saveCalls == 0; i++) await Task.Delay(25);

        saveCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecWrite_does_not_block_caller()
    {
        Func<Task> slowPersist = async () => { await Task.Delay(500); };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Make(_ui, slowPersist).ExecWrite();
        sw.Stop();

        // Should return immediately (fire-and-forget).
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200));
    }
}
