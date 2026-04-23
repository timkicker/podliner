namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdHistoryHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.History;
    public void Handle(CmdParsed cmd, CmdContext ctx) => ctx.Cases.History.Exec(cmd.Args);
}
