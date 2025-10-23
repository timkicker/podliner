using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdFeedsHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.AddFeed or TopCommand.Feed or TopCommand.RemoveFeed;
    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.AddFeed:   CmdFeedsModule.ExecAddFeed(cmd.Args, ctx.UI); return;
            case TopCommand.Feed:      CmdFeedsModule.ExecFeed(cmd.Args, ctx.UI, ctx.Data, ctx.Persist); return;
            case TopCommand.RemoveFeed: CmdFeedsModule.RemoveSelectedFeed(ctx.UI, ctx.Data, ctx.Persist); return;
        }
    }
}