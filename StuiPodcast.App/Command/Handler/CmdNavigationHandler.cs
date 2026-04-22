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
        var ui = ctx.Ui; var data = ctx.Data; var ep = ctx.Episodes;
        switch (cmd.Kind)
        {
            case TopCommand.Next: CmdNavigationModule.SelectRelative(+1, ui, data, ep); return;
            case TopCommand.Prev: CmdNavigationModule.SelectRelative(-1, ui, data, ep); return;
            case TopCommand.Goto: CmdNavigationModule.ExecGoto(cmd.Args, ui, data, ep); return;
            case TopCommand.VimTop:    CmdNavigationModule.SelectAbsolute(0, ui, data, ep); return;
            case TopCommand.VimMiddle: CmdNavigationModule.SelectMiddle(ui, data, ep);     return;
            case TopCommand.VimBottom: CmdNavigationModule.SelectAbsolute(int.MaxValue, ui, data, ep); return;
            case TopCommand.NextUnplayed: CmdNavigationModule.JumpUnplayed(+1, ui, ctx.Playback, data, ep); return;
            case TopCommand.PrevUnplayed: CmdNavigationModule.JumpUnplayed(-1, ui, ctx.Playback, data, ep); return;
        }
    }
}
