namespace StuiPodcast.App.Command;

internal sealed class CmdParsed
{
    public string Raw { get; }
    public string Cmd { get; }
    public string[] Args { get; }
    public TopCommand Kind { get; }
    public CmdParsed(string raw, string cmd, string[] args, TopCommand kind)
    { Raw = raw; Cmd = cmd; Args = args; Kind = kind; }
}
