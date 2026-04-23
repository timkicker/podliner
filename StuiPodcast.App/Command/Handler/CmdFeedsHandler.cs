namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdFeedsHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.AddFeed or TopCommand.Feed or TopCommand.RemoveFeed;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var feed = ctx.Cases.Feed;
        switch (cmd.Kind)
        {
            case TopCommand.AddFeed:    feed.ExecAddFeed(cmd.Args); return;
            case TopCommand.Feed:       feed.ExecFeed(cmd.Args); return;
            case TopCommand.RemoveFeed: feed.RemoveSelectedFeed(); return;
        }
    }
}
