using FluentAssertions;
using StuiPodcast.App.Services;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class UndoStackTests
{
    [Fact]
    public void Pop_empty_returns_null()
    {
        new UndoStack().Pop().Should().BeNull();
    }

    [Fact]
    public void Pop_runs_LIFO()
    {
        var order = new List<int>();
        var s = new UndoStack();
        s.Push("first", () => order.Add(1));
        s.Push("second", () => order.Add(2));
        s.Push("third", () => order.Add(3));

        s.Pop().Should().Be("third");
        s.Pop().Should().Be("second");
        s.Pop().Should().Be("first");
        order.Should().Equal(3, 2, 1);
    }

    [Fact]
    public void Bounded_ring_drops_oldest()
    {
        var s = new UndoStack(capacity: 2);
        s.Push("a", () => { });
        s.Push("b", () => { });
        s.Push("c", () => { });

        s.Count.Should().Be(2);
        s.Pop().Should().Be("c");
        s.Pop().Should().Be("b");
        s.Pop().Should().BeNull();
    }

    [Fact]
    public void Callback_exception_is_surfaced_and_entry_discarded()
    {
        var s = new UndoStack();
        s.Push("fails", () => throw new InvalidOperationException("nope"));

        var result = s.Pop();
        result.Should().Contain("undo failed");
        result.Should().Contain("nope");
        s.Count.Should().Be(0);
    }

    [Fact]
    public void Peek_shows_description_without_popping()
    {
        var s = new UndoStack();
        s.Push("label", () => { });
        s.PeekDescription().Should().Be("label");
        s.Count.Should().Be(1);
    }

    [Fact]
    public void Clear_removes_everything()
    {
        var s = new UndoStack();
        s.Push("a", () => { });
        s.Push("b", () => { });
        s.Clear();
        s.Count.Should().Be(0);
        s.Pop().Should().BeNull();
    }

    [Fact]
    public void Push_rejects_null_callback_and_empty_description()
    {
        var s = new UndoStack();
        Action nullCb    = () => s.Push("desc", null!);
        Action emptyDesc = () => s.Push("", () => { });
        nullCb.Should().Throw<ArgumentNullException>();
        emptyDesc.Should().Throw<ArgumentException>();
    }
}
