namespace Podliner.App.Bootstrap;

// What podliner prints when it cannot start at all.
internal static class StartupMessages
{
    // No VLC, mpv or ffplay: used to be an unhandled exception, exit 134 and a
    // stack trace. Not having one is the likeliest way to hit this, e.g. the
    // AUR package without its optional dependencies.
    public const int NoAudioEngineExitCode = 1;

    public const string NoAudioEngine =
        "podliner needs an audio player and found none.\n" +
        "Install one of vlc, mpv or ffmpeg (for ffplay), for example:\n" +
        "  Arch:           sudo pacman -S vlc\n" +
        "  Debian/Ubuntu:  sudo apt install vlc\n" +
        "  macOS:          brew install --cask vlc\n" +
        "On Windows VLC is bundled; if you see this there, please open an issue.";
}
