using Podliner.Core;

namespace Podliner.App.Services;

// Decides which URL an episode is actually played from. Extracted out of
// UiPlaybackWiring so the rules can be tested without a UI, a player or a
// download manager, the same way DownloadRetryPolicy and NetworkFlipPolicy
// were pulled out of their orchestrators.
//
// Three modes, set with `:play-source`:
//   local  — only ever the downloaded file, even when online.
//   remote — only ever the feed URL, even when a local copy exists.
//   auto   — prefer the local file, fall back to the network when online.
//
// A null result means "nothing playable": the usual case is being offline
// with no download, and the caller turns that into an OSD rather than
// handing the engine a URL that cannot resolve.
internal static class PlaySourceResolver
{
    public static string? Resolve(AppData data, string? localPath, Episode ep)
    {
        var mode = (data.PlaySource ?? "auto").Trim().ToLowerInvariant();
        var online = data.NetworkOnline;

        return mode switch
        {
            "local"  => localPath,
            "remote" => ep.AudioUrl,
            _        => localPath ?? (online ? ep.AudioUrl : null)
        };
    }
}
