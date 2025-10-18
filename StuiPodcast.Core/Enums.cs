// Models.cs
using System;
using System.Collections.Generic;
namespace StuiPodcast.Core;

[Flags]
public enum PlayerCapabilities {
    None     = 0,
    Play     = 1 << 0,
    Pause    = 1 << 1,
    Stop     = 1 << 2,
    Seek     = 1 << 3,     // präzises Seek während der Wiedergabe
    Volume   = 1 << 4,     // Lautstärke live veränderbar
    Speed    = 1 << 5,     // Wiedergabegeschwindigkeit live veränderbar
    Network  = 1 << 6,     // Remote-URLs unterstützt
    Local    = 1 << 7      // lokale Dateien unterstützt
}






// Netzprofil für Start-/Buffer-Verhalten (Engines lesen dies perspektivisch aus)
public enum NetworkProfile {
    Standard = 0,
    BadNetwork = 1
}


