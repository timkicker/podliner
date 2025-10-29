using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdNetStateHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Net or TopCommand.PlaySource or TopCommand.Save;
    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var ui = ctx.Ui; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Net:         CmdNetModule.ExecNet(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.PlaySource:  CmdNetModule.ExecPlaySource(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.Save:        CmdStateModule.ExecSave(cmd.Args, ui, data, ctx.Persist); return;
        }
    }
}