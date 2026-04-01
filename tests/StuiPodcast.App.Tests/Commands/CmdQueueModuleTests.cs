using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdQueueModuleTests
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private bool _saved;
    private Task SaveAsync() { _saved = true; return Task.CompletedTask; }

    private Episode MakeEpisode()
    {
        var ep = new Episode { Title = "Test", AudioUrl = "https://x.com/e.mp3" };
        _data.Episodes.Add(ep);
        _ui.SelectedEpisode = ep;
        return ep;
    }

    [Fact]
    public void Returns_false_for_unrelated_command()
    {
        CmdQueueModule.HandleQueue(":help", _ui, _data, SaveAsync).Should().BeFalse();
    }

    [Fact]
    public void Add_adds_episode_to_queue()
    {
        var ep = MakeEpisode();
        CmdQueueModule.HandleQueue(":queue add", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().Contain(ep.Id);
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Toggle_removes_if_already_queued()
    {
        var ep = MakeEpisode();
        _data.Queue.Add(ep.Id);
        CmdQueueModule.HandleQueue(":queue toggle", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().NotContain(ep.Id);
    }

    [Fact]
    public void Toggle_adds_if_not_queued()
    {
        var ep = MakeEpisode();
        CmdQueueModule.HandleQueue(":queue toggle", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().Contain(ep.Id);
    }

    [Fact]
    public void Remove_removes_from_queue()
    {
        var ep = MakeEpisode();
        _data.Queue.Add(ep.Id);
        CmdQueueModule.HandleQueue(":queue rm", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().NotContain(ep.Id);
    }

    [Fact]
    public void Clear_empties_queue()
    {
        var ep = MakeEpisode();
        _data.Queue.Add(ep.Id);
        _data.Queue.Add(Guid.NewGuid());
        CmdQueueModule.HandleQueue(":queue clear", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().BeEmpty();
    }

    [Fact]
    public void Uniq_removes_duplicates()
    {
        var ep = MakeEpisode();
        _data.Queue.Add(ep.Id);
        _data.Queue.Add(ep.Id);
        _data.Queue.Add(ep.Id);
        CmdQueueModule.HandleQueue(":queue uniq", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().HaveCount(1);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("uniq"));
    }

    [Fact]
    public void Shuffle_preserves_all_elements()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        _data.Queue.AddRange(ids);
        var ep = MakeEpisode();

        CmdQueueModule.HandleQueue(":queue shuffle", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().BeEquivalentTo(ids);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("shuffled"));
    }

    [Fact]
    public void Move_down_moves_episode_one_position()
    {
        var ep1 = new Episode { Title = "E1", AudioUrl = "https://x.com/1.mp3" };
        var ep2 = new Episode { Title = "E2", AudioUrl = "https://x.com/2.mp3" };
        _data.Episodes.AddRange(new[] { ep1, ep2 });
        _data.Queue.AddRange(new[] { ep1.Id, ep2.Id });
        _ui.SelectedEpisode = ep1;

        CmdQueueModule.HandleQueue(":queue move down", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().Equal(ep2.Id, ep1.Id);
    }

    [Fact]
    public void Move_top_moves_to_front()
    {
        var ep1 = new Episode { Title = "E1", AudioUrl = "https://x.com/1.mp3" };
        var ep2 = new Episode { Title = "E2", AudioUrl = "https://x.com/2.mp3" };
        var ep3 = new Episode { Title = "E3", AudioUrl = "https://x.com/3.mp3" };
        _data.Episodes.AddRange(new[] { ep1, ep2, ep3 });
        _data.Queue.AddRange(new[] { ep1.Id, ep2.Id, ep3.Id });
        _ui.SelectedEpisode = ep3;

        CmdQueueModule.HandleQueue(":queue move top", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue[0].Should().Be(ep3.Id);
    }

    [Fact]
    public void Q_shortcut_acts_as_queue_add()
    {
        var ep = MakeEpisode();
        CmdQueueModule.HandleQueue("q", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().Contain(ep.Id);
    }

    [Fact]
    public void No_selected_episode_is_noop()
    {
        _ui.SelectedEpisode = null;
        CmdQueueModule.HandleQueue(":queue add", _ui, _data, SaveAsync).Should().BeTrue();
        _data.Queue.Should().BeEmpty();
    }
}
