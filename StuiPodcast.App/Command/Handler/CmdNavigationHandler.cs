using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdNavigationHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Next or TopCommand.Prev or TopCommand.Goto
            or TopCommand.VimTop or TopCommand.VimMiddle or TopCommand.VimBottom
            or TopCommand.NextUnplayed or TopCommand.PrevUnplayed;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Next: CmdNavigationModule.SelectRelative(+1, ui, data); return;
            case TopCommand.Prev: CmdNavigationModule.SelectRelative(-1, ui, data); return;
            case TopCommand.Goto: CmdNavigationModule.ExecGoto(cmd.Args, ui, data); return;
            case TopCommand.VimTop:    CmdNavigationModule.SelectAbsolute(0, ui, data); return;
            case TopCommand.VimMiddle: CmdNavigationModule.SelectMiddle(ui, data);     return;
            case TopCommand.VimBottom: CmdNavigationModule.SelectAbsolute(int.MaxValue, ui, data); return;
            case TopCommand.NextUnplayed: CmdNavigationModule.JumpUnplayed(+1, ui, ctx.Playback, data); return;
            case TopCommand.PrevUnplayed: CmdNavigationModule.JumpUnplayed(-1, ui, ctx.Playback, data); return;
        }
    }
}