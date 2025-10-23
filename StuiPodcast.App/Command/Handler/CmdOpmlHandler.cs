using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdOpmlHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Opml;
    public void Handle(CmdParsed cmd, CmdContext ctx)
        => CmdOpmlModule.ExecOpml(cmd.Args, ctx.UI, ctx.Data, ctx.Persist);
}