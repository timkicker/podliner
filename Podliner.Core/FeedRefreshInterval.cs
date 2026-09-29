namespace Podliner.Core
{
    // Limits for the automatic feed refresh (#32). Here rather than next to
    // the scheduling policy in App because ConfigStore in Infra has to apply
    // the same rules on load, and Core is what both of them can see.
    public static class FeedRefreshInterval
    {
        public const int DefaultMinutes = 60;
        public const int MinMinutes = 5;             // do not hammer anyone's server
        public const int MaxMinutes = 7 * 24 * 60;   // a week

        // 0 or less means off. Anything else is held between five minutes and
        // a week, so a typo in appsettings.json cannot become a request a second.
        public static int Normalize(int minutes)
            => minutes <= 0 ? 0 : System.Math.Clamp(minutes, MinMinutes, MaxMinutes);
    }
}
