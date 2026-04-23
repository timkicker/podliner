using Serilog;

namespace StuiPodcast.App.Services;

// Bounded LIFO of undoable actions. Each entry is opaque: a short label
// for the OSD + a callback that performs the inverse. Operations that
// want to be undoable push before they mutate; :undo pops and runs.
// Kept in-memory only (session-scoped); persisting undo state across
// restarts is more surprise than benefit.
internal sealed class UndoStack
{
    public sealed record Entry(string Description, Action Undo);

    readonly int _capacity;
    readonly LinkedList<Entry> _stack = new();

    public UndoStack(int capacity = 10)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _stack.Count;

    public void Push(string description, Action undo)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("description required", nameof(description));
        if (undo == null) throw new ArgumentNullException(nameof(undo));

        _stack.AddFirst(new Entry(description, undo));
        while (_stack.Count > _capacity)
            _stack.RemoveLast();
    }

    // Pops + runs the most recent entry. Returns null if stack is empty
    // or the label of the reverted action on success. Callback exceptions
    // are logged and the entry is discarded (we don't push it back — the
    // user should be able to pop the next one rather than get stuck on a
    // corrupt entry forever).
    public string? Pop()
    {
        if (_stack.First is null) return null;
        var entry = _stack.First.Value;
        _stack.RemoveFirst();
        try { entry.Undo(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "undo entry failed desc={Desc}", entry.Description);
            return $"undo failed: {ex.Message}";
        }
        return entry.Description;
    }

    public void Clear() => _stack.Clear();

    public string? PeekDescription() => _stack.First?.Value.Description;
}
