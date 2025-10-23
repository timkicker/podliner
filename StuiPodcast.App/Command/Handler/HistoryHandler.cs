using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class HistoryHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.History;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
        => HistoryModule.ExecHistory(cmd.Args, ctx.UI, ctx.Data, ctx.Persist);
}