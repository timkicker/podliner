namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdIoHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Open or TopCommand.Copy;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.Open: ctx.Cases.Io.ExecOpen(cmd.Args); return;
            case TopCommand.Copy: ctx.Cases.Io.ExecCopy(cmd.Args); return;
        }
    }
}
