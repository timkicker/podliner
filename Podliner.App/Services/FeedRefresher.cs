using Podliner.Core;
using Podliner.Infra;
using Serilog;

namespace Podliner.App.Services;

internal enum RefreshResult { Ran, NotDue, Skipped }

internal sealed record RefreshOutcome(RefreshResult Result, IReadOnlyList<string> Failures)
{
    public static readonly RefreshOutcome NotDue  = new(RefreshResult.NotDue,  Array.Empty<string>());
    public static readonly RefreshOutcome Skipped = new(RefreshResult.Skipped, Array.Empty<string>());
}

// Runs feed refresh passes: on a timer when one is due, and on :refresh.
//
// #32: feeds were only fetched when someone typed :refresh, so a library
// could sit on months-old episodes with nothing on screen to say so. This
// owns the one pass both paths share, remembers when a pass last got
// anything through (persisted, so the interval holds across restarts), and
// counts what failed so the sidebar can show it.
internal sealed class FeedRefresher
{
    readonly IFeedService _feeds;
    readonly IFeedStore _feedStore;
    readonly AppData _data;
    readonly Func<Task> _save;
    readonly Func<DateTimeOffset> _clock;

    int _running;
    DateTimeOffset? _lastAttempt;
    List<string>? _collecting;      // failures of the pass in flight

    public FeedRefresher(IFeedService feeds, IFeedStore feedStore, AppData data,
                         Func<Task> save, Func<DateTimeOffset>? clock = null)
    {
        _feeds = feeds;
        _feedStore = feedStore;
        _data = data;
        _save = save;
        _clock = clock ?? (() => DateTimeOffset.Now);

        // The last pass before a restart counts, or every start would fetch
        // every feed however recently that already happened.
        _lastAttempt = data.LastRefreshAt;

        _feeds.FeedRefreshFailed += (feed, reason) =>
        {
            var into = Volatile.Read(ref _collecting);
            if (into == null) return;
            lock (into) into.Add($"{feed.Title}: {reason}");
        };
    }

    // Raised when a pass really starts (not when one is skipped) and after
    // every pass, manual or timed, so the lists and the sidebar title can be
    // repainted from one place.
    public event Action? Started;
    public event Action? Finished;

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public int LastFailedCount { get; private set; }

    public bool IsDue()
        => FeedRefreshPolicy.IsDue(_clock(), _lastAttempt, _data.RefreshIntervalMinutes,
                                   _data.NetworkOnline, IsRunning);

    // The timer's entry point: a pass only when one is due.
    public Task<RefreshOutcome> TickAsync()
        => IsDue() ? RunAsync() : Task.FromResult(RefreshOutcome.NotDue);

    // One pass. :refresh calls this directly: someone asking overrides the
    // interval, including an interval of off.
    public async Task<RefreshOutcome> RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return RefreshOutcome.Skipped;

        var failures = new List<string>();
        Volatile.Write(ref _collecting, failures);
        var total = _feedStore.Count;

        try { Started?.Invoke(); }
        catch (Exception ex) { Log.Debug(ex, "refresh/started subscriber threw"); }

        try
        {
            // Stamped before the fetch so a pass that fails is still not
            // retried on every tick.
            _lastAttempt = _clock();

            int failed;
            try
            {
                await _feeds.RefreshAllAsync().ConfigureAwait(false);
                lock (failures) failed = failures.Count;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "refresh/pass failed as a whole");
                lock (failures) { failures.Clear(); failures.Add($"refresh failed ({ex.Message})"); }
                failed = Math.Max(total, 1);
            }

            LastFailedCount = failed;

            // Fresh unless every feed failed. A pass with nothing to fetch
            // still counts, so an empty library does not read "never".
            if (total == 0 || failed < total)
            {
                _data.LastRefreshAt = _clock();
                try { await _save().ConfigureAwait(false); }
                catch (Exception ex) { Log.Debug(ex, "refresh/save after pass failed"); }
            }

            Log.Information("refresh/pass done feeds={Total} failed={Failed}", total, failed);
            lock (failures) return new RefreshOutcome(RefreshResult.Ran, failures.ToList());
        }
        finally
        {
            Volatile.Write(ref _collecting, null);
            Volatile.Write(ref _running, 0);
            try { Finished?.Invoke(); }
            catch (Exception ex) { Log.Debug(ex, "refresh/finished subscriber threw"); }
        }
    }

    // "Feeds", "Feeds · refreshing", "Feeds · never", "Feeds · 12m ago" or
    // "Feeds · 12m · 2 failed". A first pass over slow feeds can take a
    // minute (30s timeout each), and "never" through all of it read as if
    // nothing was happening.
    public string SidebarTitle(string separator)
    {
        if (_feedStore.Count == 0) return "Feeds";
        if (IsRunning) return $"Feeds {separator} refreshing";
        return FeedRefreshPolicy.SidebarTitle(_data.LastRefreshAt, _clock(), LastFailedCount, separator);
    }
}
