using Serilog;
using StuiPodcast.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using VLC = LibVLCSharp.Shared;

namespace StuiPodcast.Infra.Player
{
    // --- öffentliche Player-API (unverändert) -------------------------------
    public interface IPlayer : IDisposable
    {
        event Action<PlayerState>? StateChanged;
        PlayerState State { get; }
        string Name { get; }                      // Anzeigename der Engine
        PlayerCapabilities Capabilities { get; }  // Fähigkeiten

        void Play(string url, long? startMs = null);
        void TogglePause();
        void SeekRelative(TimeSpan delta);
        void SeekTo(TimeSpan position);
        void SetVolume(int vol0to100);
        void SetSpeed(double speed);
        void Stop();
    }

    
}
