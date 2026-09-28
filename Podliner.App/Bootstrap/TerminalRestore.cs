namespace Podliner.App.Bootstrap;

// Hands the terminal back to the shell: mouse tracking off, bracketed paste
// off, cursor on, attributes reset, and out of the alternate screen.
//
// Issue #34. This used to happen only at the very end of the exit cleanup,
// after player, downloader, MPRIS, the gPodder push and a flush save. A 1.5s
// watchdog in QuitApp calls Environment.Exit whenever that chain runs long,
// and the gPodder push always did, so the process died with every mode still
// on and the shell printed mouse reports as text. It now runs first, from a
// ProcessExit hook as well, and from any thread.
internal static class TerminalRestore
{
    internal const string Sequence =
        "\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l" + // mouse tracking and its encodings
        "\x1b[?2004l" +                                              // bracketed paste
        "\x1b[?25h"   +                                              // cursor visible
        "\x1b[0m"     +                                              // attributes
        "\x1b[?1049l";                                               // leave the alternate screen, last

    public static void Restore() => Restore(Console.Out);

    public static void Restore(TextWriter writer)
    {
        try
        {
            writer.Write(Sequence);
            writer.Flush();
        }
        catch
        {
            // Runs while the process is dying. A closed stdout has nothing to
            // restore, and throwing here would cut off the rest of the cleanup.
        }
    }
}
