namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdNetStateHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Net or TopCommand.PlaySource or TopCommand.Save;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.Net:         ctx.Cases.Net.ExecNet(cmd.Args); return;
            case TopCommand.PlaySource:  ctx.Cases.Net.ExecPlaySource(cmd.Args); return;
            case TopCommand.Save:        ctx.Cases.State.ExecSave(cmd.Args); return;
        }
    }
}
