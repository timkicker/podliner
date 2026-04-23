using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using StuiPodcast.Infra.Feeds;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

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
}
