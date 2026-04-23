using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Services;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdQueueModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly FakeEpisodeStore _episodes = new();
    private readonly FakeQueueService _queue = new();
    private readonly UndoStack _undo = new();
    private readonly QueueUseCase _sut;
    private bool _saved;
    private Task SaveAsync() { _saved = true; return Task.CompletedTask; }

    public CmdQueueModuleTests()
    {
        _sut = new QueueUseCase(_ui, SaveAsync, _episodes, _queue, _undo);
    }

    private Episode MakeEpisode()
    {
        var ep = new Episode { Id = Guid.NewGuid(), Title = "Test", AudioUrl = "https://x.com/e.mp3" };
        _episodes.Seed(ep);
        _ui.SelectedEpisode = ep;
        return ep;
    }

    [Fact]
    public void Returns_false_for_unrelated_command()
    {
        _sut.Handle(":help").Should().BeFalse();
    }

    [Fact]
    public void Add_adds_episode_to_queue()
    {
        var ep = MakeEpisode();
        _sut.Handle(":queue add").Should().BeTrue();
        _queue.Snapshot().Should().Contain(ep.Id);
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Toggle_removes_if_already_queued()
    {
        var ep = MakeEpisode();
        _queue.Seed(ep.Id);
        _sut.Handle(":queue toggle").Should().BeTrue();
        _queue.Snapshot().Should().NotContain(ep.Id);
    }

    [Fact]
    public void Toggle_adds_if_not_queued()
    {
        var ep = MakeEpisode();
        _sut.Handle(":queue toggle").Should().BeTrue();
        _queue.Snapshot().Should().Contain(ep.Id);
    }

    [Fact]
    public void Remove_removes_from_queue()
    {
        var ep = MakeEpisode();
        _queue.Seed(ep.Id);
        _sut.Handle(":queue rm").Should().BeTrue();
        _queue.Snapshot().Should().NotContain(ep.Id);
    }

    [Fact]
    public void Clear_empties_queue()
    {
        var ep = MakeEpisode();
        _queue.Seed(ep.Id, Guid.NewGuid());
        _sut.Handle(":queue clear").Should().BeTrue();
        _queue.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Clear_pushes_undo_that_restores_queue()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _queue.Seed(a, b);

        _sut.Handle(":queue clear").Should().BeTrue();
        _queue.Snapshot().Should().BeEmpty();
        _undo.Count.Should().Be(1);

        _undo.Pop().Should().Contain("restore queue");
        _queue.Snapshot().Should().BeEquivalentTo(new[] { a, b });
    }

    [Fact]
    public void Clear_empty_queue_does_not_push_undo()
    {
        _sut.Handle(":queue clear").Should().BeTrue();
        _undo.Count.Should().Be(0);
    }

    [Fact]
    public void Uniq_removes_duplicates()
    {
        var ep = MakeEpisode();
        _queue.Seed(ep.Id, ep.Id, ep.Id);
        _sut.Handle(":queue uniq").Should().BeTrue();
        _queue.Snapshot().Should().HaveCount(1);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("uniq"));
    }

    [Fact]
    public void Shuffle_preserves_all_elements()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        _queue.Seed(ids.ToArray());
        var ep = MakeEpisode();

        _sut.Handle(":queue shuffle").Should().BeTrue();
        _queue.Snapshot().Should().BeEquivalentTo(ids);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("shuffled"));
    }

    [Fact]
    public void Move_down_moves_episode_one_position()
    {
        var ep1 = new Episode { Id = Guid.NewGuid(), Title = "E1", AudioUrl = "https://x.com/1.mp3" };
        var ep2 = new Episode { Id = Guid.NewGuid(), Title = "E2", AudioUrl = "https://x.com/2.mp3" };
        _episodes.Seed(ep1, ep2);
        _queue.Seed(ep1.Id, ep2.Id);
        _ui.SelectedEpisode = ep1;

        _sut.Handle(":queue move down").Should().BeTrue();
        _queue.Snapshot().Should().Equal(ep2.Id, ep1.Id);
    }

    [Fact]
    public void Move_top_moves_to_front()
    {
        var ep1 = new Episode { Id = Guid.NewGuid(), Title = "E1", AudioUrl = "https://x.com/1.mp3" };
        var ep2 = new Episode { Id = Guid.NewGuid(), Title = "E2", AudioUrl = "https://x.com/2.mp3" };
        var ep3 = new Episode { Id = Guid.NewGuid(), Title = "E3", AudioUrl = "https://x.com/3.mp3" };
        _episodes.Seed(ep1, ep2, ep3);
        _queue.Seed(ep1.Id, ep2.Id, ep3.Id);
        _ui.SelectedEpisode = ep3;

        _sut.Handle(":queue move top").Should().BeTrue();
        _queue.Snapshot()[0].Should().Be(ep3.Id);
    }

    [Fact]
    public void Q_shortcut_acts_as_queue_add()
    {
        var ep = MakeEpisode();
        _sut.Handle("q").Should().BeTrue();
        _queue.Snapshot().Should().Contain(ep.Id);
    }

    [Fact]
    public void No_selected_episode_is_noop()
    {
        _ui.SelectedEpisode = null;
        _sut.Handle(":queue add").Should().BeTrue();
        _queue.Snapshot().Should().BeEmpty();
    }
}
