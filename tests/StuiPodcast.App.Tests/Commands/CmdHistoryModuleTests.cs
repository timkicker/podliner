using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdHistoryModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly FakeFeedStore _feeds = new();
    private readonly HistoryUseCase _sut;

    public CmdHistoryModuleTests()
    {
        Task SaveAsync() => Task.CompletedTask;
        var view = new ViewUseCase(_ui, _data, SaveAsync, _episodes, _feeds);
        _sut = new HistoryUseCase(_ui, _data, SaveAsync, _episodes, view);
    }

    [Fact]
    public void Clear_resets_all_play_timestamps()
    {
        _episodes.Seed(new Episode
        {
            Id = Guid.NewGuid(),
            Title = "E1", AudioUrl = "x",
            Progress = new EpisodeProgress { LastPlayedAt = DateTimeOffset.UtcNow }
        });
        _episodes.Seed(new Episode
        {
            Id = Guid.NewGuid(),
            Title = "E2", AudioUrl = "y",
            Progress = new EpisodeProgress { LastPlayedAt = DateTimeOffset.UtcNow }
        });

        _sut.Exec(new[] { "clear" });

        _episodes.Snapshot().Should().OnlyContain(e => e.Progress.LastPlayedAt == null);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("cleared") && m.Text.Contains("2"));
    }

    [Fact]
    public void Size_sets_history_size()
    {
        _sut.Exec(new[] { "size", "50" });
        _data.HistorySize.Should().Be(50);
        _ui.LastHistoryLimit.Should().Be(50);
    }

    [Fact]
    public void Size_clamps_to_minimum_10()
    {
        _sut.Exec(new[] { "size", "1" });
        _data.HistorySize.Should().Be(10);
    }

    [Fact]
    public void Size_clamps_to_maximum_10000()
    {
        _sut.Exec(new[] { "size", "99999" });
        _data.HistorySize.Should().Be(10000);
    }

    [Fact]
    public void Size_without_number_shows_usage()
    {
        _sut.Exec(new[] { "size" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }

    [Fact]
    public void Unknown_subcommand_shows_help()
    {
        _sut.Exec(new[] { "banana" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("clear") && m.Text.Contains("size"));
    }
}
