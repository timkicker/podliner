namespace Podliner.App.Bootstrap;

// Whether this process has a terminal a TUI can live in.
//
// Terminal.Gui's WindowsDriver asks Windows for the console output window in
// Application.Init and throws "The handle is invalid" when stdout is
// redirected. That exception was never caught, so podliner died with an
// unhandled .NET exception (0xE0434352, EXCEPTION_COMPLUS) whenever anything
// ran it without a console. The winget validator does exactly that, which is
// what blocked the 2.0.0 submission.
internal static class TerminalGate
{
    // Redirected output means there is nothing to draw on: a pipe, a file, or
    // an automation harness. Redirected input alone is fine, people pipe into
    // interactive programs all the time and the screen still exists.
    public static bool CanHostTui(bool outputRedirected, bool errorRedirected)
        => !(outputRedirected && errorRedirected);

    public static bool CanHostTui()
        => CanHostTui(Console.IsOutputRedirected, Console.IsErrorRedirected);

    public const string NoTerminalMessage =
        "podliner is a terminal application and needs a console to draw on.\n" +
        "Run it from a terminal, or use --version / --help without one.";
}
