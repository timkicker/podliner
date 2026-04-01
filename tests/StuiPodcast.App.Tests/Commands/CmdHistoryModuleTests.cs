using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdHistoryModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private Task SaveAsync() => Task.CompletedTask;

    [Fact]
    public void Clear_resets_all_play_timestamps()
    {
        _data.Episodes.Add(new Episode
        {
            Title = "E1", AudioUrl = "x",
            Progress = new EpisodeProgress { LastPlayedAt = DateTimeOffset.UtcNow }
        });
        _data.Episodes.Add(new Episode
        {
            Title = "E2", AudioUrl = "y",
            Progress = new EpisodeProgress { LastPlayedAt = DateTimeOffset.UtcNow }
        });

        CmdHistoryModule.ExecHistory(new[] { "clear" }, _ui, _data, SaveAsync);

        _data.Episodes.Should().OnlyContain(e => e.Progress.LastPlayedAt == null);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("cleared") && m.Text.Contains("2"));
    }

    [Fact]
    public void Size_sets_history_size()
    {
        CmdHistoryModule.ExecHistory(new[] { "size", "50" }, _ui, _data, SaveAsync);
        _data.HistorySize.Should().Be(50);
        _ui.LastHistoryLimit.Should().Be(50);
    }

    [Fact]
    public void Size_clamps_to_minimum_10()
    {
        CmdHistoryModule.ExecHistory(new[] { "size", "1" }, _ui, _data, SaveAsync);
        _data.HistorySize.Should().Be(10);
    }

    [Fact]
    public void Size_clamps_to_maximum_10000()
    {
        CmdHistoryModule.ExecHistory(new[] { "size", "99999" }, _ui, _data, SaveAsync);
        _data.HistorySize.Should().Be(10000);
    }

    [Fact]
    public void Size_without_number_shows_usage()
    {
        CmdHistoryModule.ExecHistory(new[] { "size" }, _ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Unknown_subcommand_shows_help()
    {
        CmdHistoryModule.ExecHistory(new[] { "banana" }, _ui, _data, SaveAsync);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("clear") && m.Text.Contains("size"));
    }
}
