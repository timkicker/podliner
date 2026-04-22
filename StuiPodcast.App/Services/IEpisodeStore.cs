using StuiPodcast.Core;

namespace StuiPodcast.App.Services;

// Single source of truth for the runtime episode list. Replaces direct
// access to `AppData.Episodes` across command modules, services and UI,
// which had three problems:
//   1. Two lists (`AppData.Episodes` + `LibraryStore.Current.Episodes`) kept
//      in sync manually — easy to forget one side.
//   2. O(n) linear scans for Id lookups even though LibraryStore already
//      kept a dictionary internally.
//   3. Concurrent mutations from UI-thread and background tasks with no
//      protection; patched ad-hoc with UI-dispatch in a few places.
//
// Contract:
//   • Snapshot() returns an immutable copy — safe to enumerate on any
//     thread while another thread mutates.
//   • TryGet / Find are O(1) via internal dictionary.
//   • Mutations are atomic; the Changed event fires AFTER the mutation
//     is visible to subsequent reads.
//   • Event handlers must not mutate the store re-entrantly (same thread
//     contract as before).
public interface IEpisodeStore
{
    // ── Reads ────────────────────────────────────────────────────────────────
    int Count { get; }
    IReadOnlyList<Episode> Snapshot();
    bool TryGet(Guid id, out Episode? ep);
    Episode? Find(Guid id);
    IReadOnlyList<Episode> WhereByFeed(Guid feedId);

    // ── Writes ──────────────────────────────────────────────────────────────
    // Upsert. Preserves user flags (Saved, Progress) for existing entries.
    Episode AddOrUpdate(Episode ep);
    bool Remove(Guid id);
    int RemoveByFeed(Guid feedId);

    // In-place property updates routed through a single code path so
    // callers don't mutate Episode instances behind the store's back.
    void SetProgress(Guid id, long posMs, DateTimeOffset? lastPlayedAt);
    void SetSaved(Guid id, bool saved);
    void SetManuallyMarkedPlayed(Guid id, bool played);

    // ── Events ──────────────────────────────────────────────────────────────
    event Action<EpisodeChange>? Changed;
}

public enum EpisodeChangeKind { Added, Updated, Removed }

public readonly record struct EpisodeChange(EpisodeChangeKind Kind, Episode Episode);
