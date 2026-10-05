namespace Podliner.App.Tests.Fakes;

static class PlaybackTestExtensions
{
    // Play, and wait until the engine has taken the episode. The coordinator
    // ignores ticks before that, as they still describe the episode before.
    public static void PlayTaken(this PlaybackCoordinator pc, Podliner.Core.Episode ep)
    {
        pc.Play(ep);
        SpinWait.SpinUntil(() => pc.EngineHasTaken, 3000);
    }
}
