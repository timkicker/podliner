namespace Podliner.App.Services;

// Which external tool gets the text, in which order.
//
// The Linux list used to be xclip then xsel. On a Wayland session xclip talks
// to an X server that is not serving the clipboard, exits 0, and changes
// nothing, so ":copy" reported success while the clipboard kept its old
// contents. wl-copy is the tool that works there.
internal static class ClipboardCommand
{
    internal readonly record struct Candidate(string File, string Arguments);

    public static IReadOnlyList<Candidate> Candidates(bool isWindows, bool isMac, bool hasWayland, string text)
    {
        if (isWindows)
            return new[] { new Candidate("powershell", $"-NoProfile -Command Set-Clipboard -Value @'\n{text}\n'@") };

        if (isMac)
            return new[] { new Candidate("pbcopy", "") };

        var xclip = new Candidate("xclip", "-selection clipboard");
        var xsel  = new Candidate("xsel", "--clipboard --input");
        var wl    = new Candidate("wl-copy", "");

        // Both can be installed side by side; the session decides which one
        // actually owns the clipboard.
        return hasWayland
            ? new[] { wl, xclip, xsel }
            : new[] { xclip, xsel, wl };
    }

    public static bool HasWaylandSession()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    // Windows takes the text as a command-line argument; the others read stdin.
    public static bool WritesToStdin(in Candidate c) => c.File != "powershell";
}
