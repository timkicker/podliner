using FluentAssertions;
using Podliner.App.Command.UseCases;
using Podliner.App.Debug;
using Podliner.App.Services;
using Podliner.App.Tests.Fakes;
using Podliner.App.UI;
using Podliner.App.UI.Wiring;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.UI;

// Found by hand with a copy of a real library: in Darknet Diaries on episode
// 8, a refresh pass finished and the selection was back on episode 1. With
// feeds now fetched every hour on their own (#32), that would pull the
// cursor away from under anyone browsing a list. This runs the real UiShell
// with the real wiring, headless.
[Collection(TuiCollection.Name)]
public sealed class RefreshKeepsSelectionTests
{
    private static readonly Guid FeedA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid FeedB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private sealed class Rig : IDisposable
    {
        public readonly TuiHarness Tui = new(140, 40);
        public readonly UiShell Ui = new(new MemoryLogSink());
        public readonly AppData Data = new() { NetworkOnline = true };
        public readonly FakeFeedService Feeds = new();
        public readonly FakeFeedStore FeedStore = new();
        public readonly FakeEpisodeStore Episodes = new();
        public readonly FeedRefresher Refresher;

        public Rig()
        {
            Ui.Build();
            FeedStore.Seed(
                new Feed { Id = FeedA, Title = "Alpha", Url = "https://ex.test/a.xml" },
                new Feed { Id = FeedB, Title = "Beta",  Url = "https://ex.test/b.xml" });
            for (int i = 1; i <= 20; i++)
                Episodes.Seed(new Episode
                {
                    Id = Guid.NewGuid(), FeedId = FeedA, Title = $"Alpha {i:00}",
                    AudioUrl = $"https://ex.test/a{i}.mp3",
                    PubDate = new DateTimeOffset(2026, 1, i, 0, 0, 0, TimeSpan.Zero),
                });

            var view = new ViewUseCase(Ui, Data, () => Task.CompletedTask, Episodes, FeedStore);
            Refresher = new FeedRefresher(Feeds, FeedStore, Data, () => Task.CompletedTask);
            UiSelectionWiring.Wire(Ui, Data, Episodes, () => Task.CompletedTask);
            UiFeedWiring.WireRefresh(Ui, Data, Refresher, FeedStore, Episodes, view);

            Ui.SetFeeds(FeedStore.Snapshot(), FeedA);
            Ui.SetEpisodesForFeed(FeedA, Episodes.Snapshot());
            Tui.Render();
            Tui.Pump();
        }

        public void Settle() { for (int i = 0; i < 4; i++) { Tui.Pump(); Tui.Render(); } }

        public void Dispose() => Tui.Dispose();
    }

    [Fact]
    public async Task A_finished_pass_leaves_the_selected_episode_selected()
    {
        using var r = new Rig();
        r.Ui.SelectEpisodeIndex(7);
        r.Settle();
        var before = r.Ui.GetSelectedEpisode();
        before.Should().NotBeNull();

        await r.Refresher.RunAsync();
        r.Settle();

        r.Ui.GetSelectedEpisode()!.Id.Should().Be(before!.Id,
            $"the cursor was on '{before.Title}' and nobody moved it");
    }

    [Fact]
    public async Task A_finished_pass_leaves_the_selected_feed_selected()
    {
        using var r = new Rig();
        r.Ui.SelectFeed(FeedB);
        r.Settle();

        await r.Refresher.RunAsync();
        r.Settle();

        r.Ui.GetSelectedFeedId().Should().Be(FeedB);
    }
}
