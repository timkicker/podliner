namespace StuiPodcast.Core
{
    public class PlayerState
    {
        public Guid? EpisodeId { get; set; }
        public bool IsPlaying { get; set; }
        public int Volume0_100 { get; set; } = 70;
        public double Speed { get; set; } = 1.0;
        public TimeSpan Position { get; set; }
        public TimeSpan? Length { get; set; }

        // capabilities supported by the active engine
        public PlayerCapabilities Capabilities { get; set; } =
            PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
            PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
            PlayerCapabilities.Network | PlayerCapabilities.Local;
    }
}