namespace StuiPodcast.App;

internal sealed class OpmlHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Opml;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
        => OpmlModule.ExecOpml(cmd.Args, ctx.UI, ctx.Data, ctx.Persist);
}