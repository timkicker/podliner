using StuiPodcast.App.Command;

namespace StuiPodcast.App;

internal sealed class CmdEngineHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Engine;
    public void Handle(CmdParsed cmd, CmdContext ctx)
        => CmdEngineModule.ExecEngine(cmd.Args, ctx.Player, ctx.UI, ctx.Data, ctx.Persist, ctx.SwitchEngine);
}