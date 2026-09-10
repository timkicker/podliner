using Podliner.App.Command;

namespace Podliner.App;

internal sealed class CmdSyncHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Sync;
    public void Handle(CmdParsed cmd, CmdContext ctx) => ctx.Cases.Sync.Exec(cmd);
}
