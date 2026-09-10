using System.Reflection;
using System.Text;
using Terminal.Gui;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// Runs Terminal.Gui headless so views can be built, laid out and rendered
// inside `dotnet test` with no terminal, no X server and no window manager.
//
// Terminal.Gui ships a `FakeDriver` for exactly this. It keeps the screen in
// `FakeDriver.Contents`, an int[row, col, 3] where index 0 is the rune, so a
// rendered frame can be read back as plain text and asserted on.
//
// `FakeMainLoop` is internal to Terminal.Gui, hence the reflected construction.
//
// Application state is process-global, so every test that uses this must own
// it exclusively. The [Collection] attribute on the test classes serialises
// them; Dispose tears the Application back down so the next test starts clean.
public sealed class TuiHarness : IDisposable
{
    public const int DefaultCols = 80;
    public const int DefaultRows = 25;

    public FakeDriver Driver { get; }

    private bool _disposed;

    public TuiHarness(int cols = DefaultCols, int rows = DefaultRows)
    {
        // A previous test may have left the Application initialised if it
        // threw before disposing. Reset first so we never stack two inits.
        TryShutdown();

        Driver = new FakeDriver();

        var loopType = typeof(Application).Assembly.GetType("Terminal.Gui.FakeMainLoop")
            ?? throw new InvalidOperationException("Terminal.Gui.FakeMainLoop not found; did the Terminal.Gui version change?");

        var loop = (IMainLoopDriver)Activator.CreateInstance(
            loopType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { Driver },
            culture: null)!;

        Application.Init(Driver, loop);

        // FakeDriver always comes up 80x25 and ignores FakeConsole until
        // SetWindowSize runs, so anything else has to be applied after Init.
        if (cols != DefaultCols || rows != DefaultRows)
            Driver.SetWindowSize(cols, rows);
    }

    // Drains the main-loop queue. UiShell marshals almost every update
    // through Application.MainLoop.Invoke, which only queues the action;
    // without a running loop those updates would never be applied and the
    // rendered screen would stay empty.
    public void Pump(int iterations = 4)
    {
        var loop = Application.MainLoop;
        if (loop == null) return;

        for (int i = 0; i < iterations; i++)
        {
            try { loop.MainIteration(); }
            catch { break; }
        }
    }

    // Lays out and paints `top`, then leaves the frame in Driver.Contents.
    public void Render(Toplevel top)
    {
        Application.Begin(top);
        Pump();
        Application.Refresh();
    }

    public void Render() => Render(Application.Top);

    // Drives a terminal resize the way the real drivers do on SIGWINCH, so
    // tests can assert the layout recovers. FakeDriver.SetWindowSize updates
    // FakeConsole, re-runs ResizeScreen and fires the TerminalResized hook
    // Application installed during Init.
    public void Resize(int cols, int rows)
    {
        Driver.SetWindowSize(cols, rows);
        Pump();
        Application.Refresh();
    }

    // ── screen readback ─────────────────────────────────────────────────────

    public int Cols => Application.Driver.Cols;
    public int Rows => Application.Driver.Rows;

    // One rendered row as text, trailing blanks removed.
    public string Line(int row)
    {
        var contents = Driver.Contents;
        var sb = new StringBuilder();
        for (int col = 0; col < Cols; col++)
            sb.Append((char)contents[row, col, 0]);
        return sb.ToString().TrimEnd();
    }

    // The whole screen as text, one entry per row.
    public string[] Lines()
    {
        var lines = new string[Rows];
        for (int r = 0; r < Rows; r++) lines[r] = Line(r);
        return lines;
    }

    public string Screen() => string.Join("\n", Lines());

    public bool ScreenContains(string needle)
        => Screen().Contains(needle, StringComparison.Ordinal);

    // Row index of the first line containing `needle`, or -1.
    public int RowOf(string needle)
    {
        for (int r = 0; r < Rows; r++)
            if (Line(r).Contains(needle, StringComparison.Ordinal)) return r;
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TryShutdown();
    }

    private static void TryShutdown()
    {
        try { Application.Shutdown(); }
        catch { /* not initialised, nothing to tear down */ }
    }
}

// Terminal.Gui's Application is a process-wide singleton, so every test
// touching it has to run serially.
[CollectionDefinition(TuiCollection.Name, DisableParallelization = true)]
public sealed class TuiCollection
{
    public const string Name = "tui";
}
