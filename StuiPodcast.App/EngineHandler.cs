namespace StuiPodcast.App;

internal sealed class EngineHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Engine;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
        => EngineModule.ExecEngine(cmd.Args, ctx.Player, ctx.UI, ctx.Data, ctx.Persist, ctx.SwitchEngine);
}