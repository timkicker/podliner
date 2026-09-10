using FluentAssertions;
using Podliner.App.Command.UseCases;
using Podliner.App.Tests.Fakes;
using Podliner.Core;
using Podliner.Infra.Feeds;
using Xunit;

namespace Podliner.App.Tests.Commands;

public sealed class ChaptersUseCaseLoadForUiTests
{
    readonly FakeUiShell _ui = new();
    readonly FakeAudioPlayer _player = new();
    readonly FakeEpisodeStore _episodes = new();

    ChaptersUseCase Make(bool online = true, Func<Guid, string?>? localPath = null)
    {
        Task Save() => Task.CompletedTask;
        var fetcher = new ChaptersFetcher(new System.Net.Http.HttpClientHandler());
        var uc = new ChaptersUseCase(_ui, _player, _episodes, fetcher, Save, localPath);
        uc.IsOnlineLookup = () => online;
        return uc;
    }

    [Fact]
    public async Task Cached_chapters_return_Loaded_without_network()
    {
        var ep = new Episode
        {
            Id = Guid.NewGuid(),
            AudioUrl = "https://example.com/ep.mp3",
            Chapters = new() { new Chapter { StartSeconds = 0, Title = "Intro" } }
        };
        var uc = Make(online: false); // network shouldn't matter
        var r = await uc.LoadForUiAsync(ep);
        r.Outcome.Should().Be(ChaptersUseCase.LoadOutcome.Loaded);
        r.Chapters.Should().HaveCount(1);
    }

    [Fact]
    public async Task Offline_with_no_local_file_returns_Offline()
    {
        var ep = new Episode { Id = Guid.NewGuid(), AudioUrl = "https://example.com/ep.mp3" };
        var uc = Make(online: false);
        var r = await uc.LoadForUiAsync(ep);
        r.Outcome.Should().Be(ChaptersUseCase.LoadOutcome.Offline);
    }

    [Fact]
    public async Task Null_episode_returns_NoSource()
    {
        var uc = Make();
        var r = await uc.LoadForUiAsync(null!);
        r.Outcome.Should().Be(ChaptersUseCase.LoadOutcome.NoSource);
    }

    // Exec dispatches its work with a bare `_ = Task.Run(...)`. CLAUDE.md
    // requires those blocks to catch and surface, because an exception on a
    // background thread otherwise leaves :chapter answering with nothing.
    [Fact]
    public async Task A_failure_in_the_background_load_reaches_the_user()
    {
        var ep = new Episode { Id = Guid.NewGuid(), AudioUrl = "https://example.com/ep.mp3" };
        _episodes.Seed(ep);
        _ui.SelectedEpisode = ep;

        var uc = Make(localPath: _ => throw new IOException("download folder is gone"));

        uc.Exec(new[] { "list" });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && _ui.OsdMessages.Count == 0)
            await Task.Delay(20);

        _ui.OsdMessages.Should().ContainSingle()
            .Which.Text.Should().StartWith("chapters: failed");
    }
}
