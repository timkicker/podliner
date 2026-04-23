namespace StuiPodcast.Infra.Player;

// Shared HTTP defaults for audio-engine network requests.
//
// CDN anti-bot filters (notably Cloudflare in front of Buzzsprout, but also
// AWS CloudFront on some feeds) return HTTP 403 for the default engine UAs
// ("VLC/3.x LibVLC/3.x", "mpv 0.x", "Lavf/..."). A browser-compatible UA
// with our honest app identifier gets past those filters while still being
// truthful about where the traffic comes from. Podcast clients typically do
// this same trick.
internal static class PlayerHttpDefaults
{
    public const string UserAgent = "Mozilla/5.0 (compatible; podliner/1.0.1)";
}
