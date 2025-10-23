namespace StuiPodcast.Core;

[Flags]
public enum PlayerCapabilities {
    None     = 0,
    Play     = 1 << 0,
    Pause    = 1 << 1,
    Stop     = 1 << 2,
    Seek     = 1 << 3,     // precise seeking during playback
    Volume   = 1 << 4,     // live volume control
    Speed    = 1 << 5,     // live playback speed
    Network  = 1 << 6,     // supports remote urls
    Local    = 1 << 7      // supports local files
}

// network profile for startup/buffering behavior (engines may read this)
public enum NetworkProfile {
    Standard = 0,
    BadNetwork = 1
}