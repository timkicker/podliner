namespace Podliner.App.Services;

// Decides when an online/offline probe result should actually flip the app's
// network state. Extracted from NetworkMonitor so the hysteresis can be
// tested without real sockets, the same way DownloadRetryPolicy was pulled
// out of DownloadManager.
//
// Three guards keep a flaky link from thrashing the UI:
//   • A run of consecutive agreeing probes is required (more to go offline
//     than to come back, because a false "offline" is the expensive one:
//     it hides remote episodes and stops downloads).
//   • A minimum dwell time since the last flip.
//   • Counters reset whenever a probe disagrees with the current run.
internal sealed class NetworkFlipPolicy
{
    public const int FailsForOffline    = 4;
    public const int SuccessesForOnline = 3;

    public static readonly TimeSpan MinDwell = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _minDwell;

    private int _ok;
    private int _fail;
    private DateTimeOffset _lastFlip = DateTimeOffset.MinValue;

    public NetworkFlipPolicy(TimeSpan? minDwell = null) => _minDwell = minDwell ?? MinDwell;

    public int Successes => _ok;
    public int Failures  => _fail;

    // Records the very first probe. It sets the counters without ever
    // reporting a flip: startup adopts whatever the probe found.
    public void Seed(bool online, DateTimeOffset now)
    {
        _ok = online ? 1 : 0;
        _fail = online ? 0 : 1;
        _lastFlip = now;
    }

    // Feeds one probe result in. Returns the new state when it should flip,
    // or null to stay put.
    public bool? Observe(bool currentState, bool probeOnline, DateTimeOffset now)
    {
        if (probeOnline) { _ok++; _fail = 0; }
        else             { _fail++; _ok = 0; }

        if (now - _lastFlip < _minDwell) return null;

        if (!currentState && probeOnline && _ok >= SuccessesForOnline)
        {
            _lastFlip = now;
            return true;
        }

        if (currentState && !probeOnline && _fail >= FailsForOffline)
        {
            _lastFlip = now;
            return false;
        }

        return null;
    }
}
