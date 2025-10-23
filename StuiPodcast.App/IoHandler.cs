namespace StuiPodcast.App;

internal sealed class IoHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Open or TopCommand.Copy;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.Open: IoModule.ExecOpen(cmd.Args, ctx.UI, ctx.Data); return;
            case TopCommand.Copy: IoModule.ExecCopy(cmd.Args, ctx.UI, ctx.Data); return;
        }
    }
}