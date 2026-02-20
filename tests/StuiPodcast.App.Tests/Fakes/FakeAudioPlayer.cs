using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Tests.Fakes;

/// <summary>
/// Minimal IAudioPlayer stub for unit tests.
/// Records the last call to SetVolume/SetSpeed and updates State directly.
/// </summary>
sealed class FakeAudioPlayer : IAudioPlayer
{
    public PlayerState State { get; set; } = new();
    public string Name => "Fake";
    public PlayerCapabilities Capabilities { get; set; } =
        PlayerCapabilities.Play  | PlayerCapabilities.Pause |
        PlayerCapabilities.Stop  | PlayerCapabilities.Seek  |
        PlayerCapabilities.Volume | PlayerCapabilities.Speed;

    public event Action<PlayerState>? StateChanged;

    public int    LastSetVolume { get; private set; }
    public double LastSetSpeed  { get; private set; }

    public void SetVolume(int v)    { LastSetVolume = v; State.Volume0_100 = v; }
    public void SetSpeed(double s)  { LastSetSpeed  = s; State.Speed = s; }
    public void TogglePause()       { State.IsPlaying = !State.IsPlaying; }
    public void Stop()              { State.IsPlaying = false; State.EpisodeId = null; }
    public void Play(string url, long? startMs = null) { }
    public void SeekTo(TimeSpan t)      { State.Position = t; }
    public void SeekRelative(TimeSpan dt) { State.Position += dt; }
    public void Dispose() { }
}
