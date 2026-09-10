using FluentAssertions;
using Podliner.App.Command.UseCases;
using Podliner.App.Services;
using Podliner.App.Tests.Fakes;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.Commands;

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
    public void No_selected_episode_is_noop()
    {
        _ui.SelectedEpisode = null;
        _sut.Handle(":queue add").Should().BeTrue();
        _queue.Snapshot().Should().BeEmpty();
    }

    // ── the bare "q" shortcut is gone ────────────────────────────────────────

    [Theory]
    [InlineData("q")]
    [InlineData("Q")]
    [InlineData("  q  ")]
    public void A_bare_q_is_not_a_queue_command(string cmd)
    {
        // "q" is the quit key. It used to double as ":queue add" here, which
        // meant the help browser documented a shortcut that quits the app
        // when pressed and queues an episode when typed.
        _sut.Handle(cmd).Should().BeFalse();
    }

    [Fact]
    public void A_bare_q_does_not_touch_the_queue()
    {
        var ep = MakeEpisode();
        _episodes.Seed(ep);
        _ui.SelectedEpisode = ep;

        _sut.Handle("q");

        _queue.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Queue_add_still_works()
    {
        var ep = MakeEpisode();
        _episodes.Seed(ep);
        _ui.SelectedEpisode = ep;

        _sut.Handle(":queue add").Should().BeTrue();

        _queue.Snapshot().Should().Contain(ep.Id);
    }

    // ── feedback and undo ───────────────────────────────────────────────────
    //
    // :queue shuffle, uniq, move and clear all report what they did; add and
    // rm said nothing, so after :queue toggle there was no way to tell which
    // way it went. And :undo promises to revert the last destructive action,
    // which a removal is.

    [Fact]
    public void Add_says_it_queued_the_episode()
    {
        MakeEpisode();

        _sut.Handle(":queue add");

        _ui.OsdMessages.Should().ContainSingle().Which.Text.Should().Be("queue: added");
    }

    [Fact]
    public void Add_on_a_queued_episode_leaves_it_queued()
    {
        var ep = MakeEpisode();
        _queue.Seed(ep.Id);

        _sut.Handle(":queue add");

        _queue.Snapshot().Should().Contain(ep.Id, "a command called add must never remove");
        _ui.OsdMessages.Should().ContainSingle().Which.Text.Should().Be("queue: already queued");
    }

    [Fact]
    public void Toggle_says_which_way_it_went()
    {
        MakeEpisode();

        _sut.Handle(":queue toggle");
        _sut.Handle(":queue toggle");

        _ui.OsdMessages.Select(m => m.Text).Should().Equal("queue: added", "queue: removed");
    }

    [Fact]
    public void Rm_says_it_removed_the_episode()
    {
        var ep = MakeEpisode();
        _queue.Append(ep.Id);

        _sut.Handle(":queue rm");

        _ui.OsdMessages.Should().ContainSingle().Which.Text.Should().Be("queue: removed");
    }

    [Fact]
    public void Rm_on_an_episode_that_is_not_queued_says_so()
    {
        MakeEpisode();

        _sut.Handle(":queue rm");

        _ui.OsdMessages.Should().ContainSingle().Which.Text.Should().Be("queue: not queued");
    }

    [Fact]
    public void Rm_can_be_undone()
    {
        var ep = MakeEpisode();
        var other = new Episode { Id = Guid.NewGuid(), Title = "Other", AudioUrl = "https://x.com/o.mp3" };
        _episodes.Seed(other);
        _queue.Append(other.Id);
        _queue.Append(ep.Id);

        _sut.Handle(":queue rm");
        _queue.Snapshot().Should().NotContain(ep.Id);

        _undo.Pop().Should().NotBeNull("a removal is a destructive action and :undo promises to revert those");
        _queue.Snapshot().Should().Equal(new[] { other.Id, ep.Id }, "the episode belongs back where it was");
    }

    [Fact]
    public void Undoing_a_removal_puts_it_back_at_its_old_position()
    {
        var first = new Episode { Id = Guid.NewGuid(), Title = "First", AudioUrl = "https://x.com/1.mp3" };
        var last  = new Episode { Id = Guid.NewGuid(), Title = "Last",  AudioUrl = "https://x.com/2.mp3" };
        _episodes.Seed(first, last);
        var ep = MakeEpisode();
        _queue.Append(first.Id);
        _queue.Append(ep.Id);
        _queue.Append(last.Id);

        _sut.Handle(":queue rm");
        _undo.Pop();

        _queue.Snapshot().Should().Equal(first.Id, ep.Id, last.Id);
    }

    [Fact]
    public void Add_is_not_undoable()
    {
        MakeEpisode();

        _sut.Handle(":queue add");

        _undo.Pop().Should().BeNull("adding is not destructive, the stack is for losing things");
    }
}
