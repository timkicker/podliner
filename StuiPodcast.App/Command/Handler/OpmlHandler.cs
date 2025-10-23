using StuiPodcast.App.Command;
using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class OpmlHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Opml;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
        => OpmlModule.ExecOpml(cmd.Args, ctx.UI, ctx.Data, ctx.Persist);
}