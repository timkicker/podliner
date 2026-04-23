using Serilog;
using Terminal.Gui;

namespace StuiPodcast.App.Services;

// Single-shot sleep timer that stops playback after a duration. Uses the
// Terminal.Gui main-loop timeout facility so the fire happens on the UI
// thread with no extra synchronisation. The timer is intentionally
// session-scoped: closing the app cancels it (matches how every other
// podcast app behaves, and avoids surprise-stops on the next launch).
internal sealed class SleepTimer
{
    readonly Action _onFire;

    object? _token;
    DateTimeOffset _endsAt;

    public SleepTimer(Action onFire)
    {
        _onFire = onFire ?? throw new ArgumentNullException(nameof(onFire));
    }

    public bool IsActive => _token != null;

    // Remaining time, or null if the timer is not armed.
    public TimeSpan? TimeLeft
        => _token is null ? null : _endsAt - DateTimeOffset.UtcNow;

    public void Set(TimeSpan duration)
    {
        Cancel();
        if (duration <= TimeSpan.Zero) return;

        _endsAt = DateTimeOffset.UtcNow + duration;
        _token = Application.MainLoop?.AddTimeout(duration, _ =>
        {
            _token = null;
            try { _onFire(); }
            catch (Exception ex) { Log.Warning(ex, "sleep-timer on-fire callback threw"); }
            return false;
        });
    }

    public void Cancel()
    {
        if (_token == null) return;
        try { Application.MainLoop?.RemoveTimeout(_token); } catch { }
        _token = null;
    }

    // "30m", "1h", "90s", "1h30m". Returns null on bad input.
    public static TimeSpan? ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToLowerInvariant();

        // Bare number = minutes (":sleep 30" == 30m).
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bare))
            return bare > 0 ? TimeSpan.FromMinutes(bare) : (TimeSpan?)null;

        double total = 0;
        int i = 0;
        bool any = false;

        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (i == start) return null;

            if (!double.TryParse(s[start..i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
                return null;
            if (i >= s.Length) return null;

            var unit = s[i++];
            total += unit switch
            {
                's' => n,
                'm' => n * 60,
                'h' => n * 3600,
                _   => double.NaN
            };
            if (double.IsNaN(total)) return null;
            any = true;
        }

        if (!any || total <= 0) return null;
        return TimeSpan.FromSeconds(total);
    }

    // "1h 2m 3s", "5m 30s", "30s". Omits zero units.
    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "0s";
        var parts = new List<string>();
        if (t.Hours   > 0) parts.Add($"{t.Hours}h");
        if (t.Minutes > 0) parts.Add($"{t.Minutes}m");
        if (t.Seconds > 0) parts.Add($"{t.Seconds}s");
        return string.Join(" ", parts);
    }
}
