using StuiPodcast.Core;

namespace StuiPodcast.App.Services;

// Owner of the playback queue. Replaces the scattered `data.Queue.X`
// mutations across CmdQueueModule, PlaybackCoordinator, UiComposer and
// the UI's SetQueueLookup. Wraps both AppData.Queue and persistence so
// there is one place that mutates queue state and emits events.
//
// The queue holds Episode IDs (not Episode references) to stay small and
// not keep removed episodes alive. Resolving IDs back to Episodes is the
// caller's job (via IEpisodeStore).
public interface IQueueService
{
    int Count { get; }
    IReadOnlyList<Guid> Snapshot();
    bool Contains(Guid id);
    int IndexOf(Guid id);

    // Mutations. Return `true` if the queue actually changed.
    bool Append(Guid id);          // adds if not already present
    bool Toggle(Guid id);          // adds if absent, removes if present
    bool Remove(Guid id);
    bool MoveToFront(Guid id);     // used by play-next semantics
    bool Move(Guid id, int toIndex);
    int Clear();
    int Dedup();                   // keep first of each id
    int Shuffle();
    // Remove the target and every entry before it (used when a queued
    // episode starts playing and trims predecessors).
    bool TrimUpToInclusive(Guid targetId);

    event Action? Changed;
}
