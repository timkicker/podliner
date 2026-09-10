using Podliner.Core;

namespace Podliner.Infra.Player;

// The order in which audio engines are tried. Split out of
// AudioPlayerFactory so the ordering can be tested without libVLC, mpv or
// ffplay being installed — engine detection is this project's most-reported
// problem area (issues #11, #18, #21, #24, #26) and until now none of it was
// covered.
//
// The factory walks this list and takes the first engine that actually
// starts, so the policy stays free of any probing.
internal static class EngineSelectionPolicy
{
    // Preference first, then the OS-appropriate fallback chain:
    //   VLC   — best capabilities everywhere, bundled on Windows.
    //   MF    — Windows only, native, but no rate control.
    //   mpv   — full features over an IPC socket.
    //   ffplay— degraded last resort: coarse seek, no live speed or volume.
    public static IReadOnlyList<AudioEngine> CandidateOrder(AudioEngine preferred, bool isWindows)
    {
        var order = new List<AudioEngine>();

        void Add(AudioEngine e)
        {
            if (e == AudioEngine.Auto) return;
            if (e == AudioEngine.MediaFoundation && !isWindows) return;
            if (!order.Contains(e)) order.Add(e);
        }

        Add(preferred);

        Add(AudioEngine.Vlc);
        if (isWindows) Add(AudioEngine.MediaFoundation);
        Add(AudioEngine.Mpv);
        Add(AudioEngine.Ffplay);

        return order;
    }

    // ffplay cannot seek precisely or change speed live, so the UI labels it
    // as a fallback rather than presenting it as a normal choice.
    public static bool IsDegraded(AudioEngine engine) => engine == AudioEngine.Ffplay;
}
