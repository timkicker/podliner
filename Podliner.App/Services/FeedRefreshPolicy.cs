namespace Podliner.App.Services;

// When feeds get fetched without anyone asking, and how stale the sidebar
// says they are.
//
// #32: feeds were only fetched on :refresh. Nothing ran on startup and
// nothing ran on a timer, so a library could sit on months-old episodes, and
// nothing on screen said so. Kept free of clocks, stores and UI so every rule
// here is a plain unit test.
internal static class FeedRefreshPolicy
{
    public const int DefaultIntervalMinutes = Podliner.Core.FeedRefreshInterval.DefaultMinutes;

    public static int Normalize(int minutes) => Podliner.Core.FeedRefreshInterval.Normalize(minutes);

    // Measured from the last attempt, not the last success: a server that is
    // down would otherwise be retried on every tick.
    public static bool IsDue(DateTimeOffset now, DateTimeOffset? lastAttempt, int intervalMinutes,
                             bool online, bool running)
    {
        if (intervalMinutes <= 0 || !online || running) return false;
        if (lastAttempt is not { } last) return true;
        if (last > now) return true;   // clock went backwards; do not wait for it
        return now - last >= TimeSpan.FromMinutes(intervalMinutes);
    }

    public static string FormatAge(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "never";
        var c = Compact(now - t);      // a future timestamp lands on "now" too
        return c == "now" ? "just now" : c + " ago";
    }

    // Terminal.Gui cuts the title of the 30 column sidebar frame at this
    // length; measured, not derived.
    public const int MaxTitleLength = 24;

    // "Feeds · 12m ago", or "Feeds · 12m · 2 failed" once something failed.
    // When both do not fit, the failures win: they are what needs noticing.
    public static string SidebarTitle(DateTimeOffset? lastSuccess, DateTimeOffset now, int failed, string separator)
    {
        if (failed <= 0)
            return $"Feeds {separator} {FormatAge(lastSuccess, now)}";

        var age = lastSuccess is { } t ? Compact(now - t) : "never";
        var full = $"Feeds {separator} {age} {separator} {failed} failed";
        if (full.Length <= MaxTitleLength) return full;

        var count = failed > 99 ? "99+" : failed.ToString();
        return $"Feeds {separator} {count} failed";
    }

    static string Compact(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1))   return $"{(int)age.TotalMinutes}m";
        if (age < TimeSpan.FromDays(1))    return $"{(int)age.TotalHours}h";
        if (age < TimeSpan.FromDays(30))   return $"{(int)age.TotalDays}d";
        if (age < TimeSpan.FromDays(365))  return $"{(int)(age.TotalDays / 30)}mo";
        return $"{(int)(age.TotalDays / 365)}y";
    }
}
