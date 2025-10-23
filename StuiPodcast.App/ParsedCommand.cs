namespace StuiPodcast.App;

internal sealed class ParsedCommand
{
    public string Raw { get; }
    public string Cmd { get; }
    public string[] Args { get; }
    public TopCommand Kind { get; }
    public ParsedCommand(string raw, string cmd, string[] args, TopCommand kind)
    { Raw = raw; Cmd = cmd; Args = args; Kind = kind; }
}
