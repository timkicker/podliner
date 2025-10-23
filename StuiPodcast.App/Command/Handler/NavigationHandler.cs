using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class NavigationHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Next or TopCommand.Prev or TopCommand.Goto
            or TopCommand.VimTop or TopCommand.VimMiddle or TopCommand.VimBottom
            or TopCommand.NextUnplayed or TopCommand.PrevUnplayed;

    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Next: NavigationModule.SelectRelative(+1, ui, data); return;
            case TopCommand.Prev: NavigationModule.SelectRelative(-1, ui, data); return;
            case TopCommand.Goto: NavigationModule.ExecGoto(cmd.Args, ui, data); return;
            case TopCommand.VimTop:    NavigationModule.SelectAbsolute(0, ui, data); return;
            case TopCommand.VimMiddle: NavigationModule.SelectMiddle(ui, data);     return;
            case TopCommand.VimBottom: NavigationModule.SelectAbsolute(int.MaxValue, ui, data); return;
            case TopCommand.NextUnplayed: NavigationModule.JumpUnplayed(+1, ui, ctx.Playback, data); return;
            case TopCommand.PrevUnplayed: NavigationModule.JumpUnplayed(-1, ui, ctx.Playback, data); return;
        }
    }
}