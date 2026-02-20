using StuiPodcast.App.Command;

namespace StuiPodcast.App;

internal sealed class CmdSyncHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Sync;
    public void Handle(CmdParsed cmd, CmdContext ctx)
        => CmdSyncModule.HandleSync(cmd, ctx);
}
