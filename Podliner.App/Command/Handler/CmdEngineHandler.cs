using Podliner.App.Command;

namespace Podliner.App;

internal sealed class CmdEngineHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Engine;
    public void Handle(CmdParsed cmd, CmdContext ctx) => ctx.Cases.Engine.Exec(cmd.Args);
}
