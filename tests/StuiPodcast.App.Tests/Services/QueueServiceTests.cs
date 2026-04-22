using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class QueueServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly LibraryStore _lib;
    private readonly QueueService _sut;

    public QueueServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-qsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lib = new LibraryStore(_dir);
        _lib.Load();
        _sut = new QueueService(_lib);
    }

    public void Dispose()
    {
        try { _lib.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Empty_queue_reports_zero_count()
    {
        _sut.Count.Should().Be(0);
        _sut.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Append_adds_new_id()
    {
        var id = Guid.NewGuid();
        _sut.Append(id).Should().BeTrue();
        _sut.Contains(id).Should().BeTrue();
        _sut.Count.Should().Be(1);
    }

    [Fact]
    public void Append_existing_is_no_op()
    {
        var id = Guid.NewGuid();
        _sut.Append(id);
        _sut.Append(id).Should().BeFalse();
        _sut.Count.Should().Be(1);
    }

    [Fact]
    public void Toggle_adds_then_removes()
    {
        var id = Guid.NewGuid();
        _sut.Toggle(id);
        _sut.Contains(id).Should().BeTrue();
        _sut.Toggle(id);
        _sut.Contains(id).Should().BeFalse();
    }

    [Fact]
    public void Remove_drops_id()
    {
        var id = Guid.NewGuid();
        _sut.Append(id);
        _sut.Remove(id).Should().BeTrue();
        _sut.Contains(id).Should().BeFalse();
    }

    [Fact]
    public void Clear_empties_queue()
    {
        _sut.Append(Guid.NewGuid());
        _sut.Append(Guid.NewGuid());
        _sut.Clear().Should().Be(2);
        _sut.Count.Should().Be(0);
    }

    [Fact]
    public void MoveToFront_reorders()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        _sut.Append(a); _sut.Append(b); _sut.Append(c);

        _sut.MoveToFront(c).Should().BeTrue();

        _sut.Snapshot().Should().Equal(c, a, b);
    }

    [Fact]
    public void Move_clamps_target()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        _sut.Append(a); _sut.Append(b); _sut.Append(c);

        _sut.Move(a, 999);
        _sut.Snapshot().Should().Equal(b, c, a);
    }

    [Fact]
    public void Dedup_removes_duplicates_keeping_first_occurrence()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        _lib.Current.Queue.AddRange(new[] { a, b, a, a, b });

        _sut.Dedup().Should().Be(3);
        _sut.Snapshot().Should().Equal(a, b);
    }

    [Fact]
    public void TrimUpToInclusive_drops_target_and_predecessors()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        _sut.Append(a); _sut.Append(b); _sut.Append(c);

        _sut.TrimUpToInclusive(b).Should().BeTrue();
        _sut.Snapshot().Should().Equal(c);
    }

    [Fact]
    public void Changed_event_fires_on_mutation()
    {
        int fires = 0;
        _sut.Changed += () => fires++;

        _sut.Append(Guid.NewGuid());
        _sut.Remove(_sut.Snapshot()[0]);

        fires.Should().Be(2);
    }

    [Fact]
    public void Mutations_persist_to_LibraryStore()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        _sut.Append(a);
        _sut.Append(b);

        _lib.Current.Queue.Should().Equal(a, b);

        _sut.Remove(a);
        _lib.Current.Queue.Should().Equal(b);
    }
}
