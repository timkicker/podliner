using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdHistoryHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.History;
    public void Handle(CmdParsed cmd, CmdContext ctx)
        => CmdHistoryModule.ExecHistory(cmd.Args, ctx.UI, ctx.Data, ctx.Persist);
}