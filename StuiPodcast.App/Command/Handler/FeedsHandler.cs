using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class FeedsHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.AddFeed or TopCommand.Feed or TopCommand.RemoveFeed;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.AddFeed:   FeedsModule.ExecAddFeed(cmd.Args, ctx.UI); return;
            case TopCommand.Feed:      FeedsModule.ExecFeed(cmd.Args, ctx.UI, ctx.Data, ctx.Persist); return;
            case TopCommand.RemoveFeed: FeedsModule.RemoveSelectedFeed(ctx.UI, ctx.Data, ctx.Persist); return;
        }
    }
}