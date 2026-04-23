namespace StuiPodcast.Core;

// Supported audio playback backends. "Auto" lets AudioPlayerFactory pick
// the best available engine via an OS-specific probe chain.
public enum AudioEngine
{
    Auto,
    Vlc,
    Mpv,
    Ffplay,
    MediaFoundation,
}

// Wire-format conversion for AudioEngine. We persist engine preferences
// as lower-case strings ("auto", "vlc", "mpv", "ffplay", "mediafoundation")
// in appsettings.json so the format is both human-readable and stable
// across enum renames. FromWire normalizes vendor aliases ("libvlc" → vlc,
// "mf" → mediafoundation) and empty/null → Auto so a missing config
// defaults sensibly.
public static class AudioEngineExt
{
    public static AudioEngine FromWire(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "vlc" or "libvlc"       => AudioEngine.Vlc,
            "mpv"                    => AudioEngine.Mpv,
            "ffplay"                 => AudioEngine.Ffplay,
            "mediafoundation" or "mf"=> AudioEngine.MediaFoundation,
            _                        => AudioEngine.Auto,
        };
    }

    public static string ToWire(this AudioEngine engine) => engine switch
    {
        AudioEngine.Vlc             => "vlc",
        AudioEngine.Mpv             => "mpv",
        AudioEngine.Ffplay          => "ffplay",
        AudioEngine.MediaFoundation => "mediafoundation",
        _                           => "auto",
    };
}
