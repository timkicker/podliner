using StuiPodcast.Core;


namespace StuiPodcast.Infra.Player
{
    public interface IAudioPlayer : IDisposable
    {
        event Action<PlayerState>? StateChanged;
        PlayerState State { get; }
        string Name { get; }                      // display name of engine
        PlayerCapabilities Capabilities { get; }  // capability flags

        void Play(string url, long? startMs = null);
        void TogglePause();
        void SeekRelative(TimeSpan delta);
        void SeekTo(TimeSpan position);
        void SetVolume(int vol0to100);
        void SetSpeed(double speed);
        void Stop();
    }
}