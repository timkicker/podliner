using FluentAssertions;
using Podliner.App.Bootstrap;
using Podliner.App.Tests.UI;
using Xunit;

namespace Podliner.App.Tests.Bootstrap;

// Once Application.Run has returned, the main loop still exists but nothing
// pumps it. DispatchToUi used to queue onto it anyway and hand back a task
// that could never complete, which is how the gPodder push on exit hung
// until the 1.5s watchdog killed podliner before it restored the terminal.
[Collection(TuiCollection.Name)]
public sealed class DispatchAfterLoopTests : IDisposable
{
    public DispatchAfterLoopTests() => Program.UiLoopStopped = false;
    public void Dispose() => Program.UiLoopStopped = false;

    [Fact]
    public void After_the_loop_has_stopped_the_action_runs_straight_away()
    {
        using var tui = new TuiHarness();   // a main loop exists, nothing pumps it
        Program.UiLoopStopped = true;
        var ran = false;

        var task = Program.DispatchToUi(() => ran = true);

        ran.Should().BeTrue("there is no loop left to run it later");
        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void While_the_loop_is_running_the_action_still_goes_through_it()
    {
        using var tui = new TuiHarness();
        var ran = false;

        var task = Program.DispatchToUi(() => ran = true);

        ran.Should().BeFalse("it is queued for the ui thread");
        tui.Pump();
        ran.Should().BeTrue();
        task.IsCompletedSuccessfully.Should().BeTrue();
    }
}
