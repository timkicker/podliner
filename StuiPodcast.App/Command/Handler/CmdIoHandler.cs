using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdIoHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Open or TopCommand.Copy;
    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.Open: CmdIoModule.ExecOpen(cmd.Args, ctx.Ui, ctx.Data); return;
            case TopCommand.Copy: CmdIoModule.ExecCopy(cmd.Args, ctx.Ui, ctx.Data); return;
        }
    }
}