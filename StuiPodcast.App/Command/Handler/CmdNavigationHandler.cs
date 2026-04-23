namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdNavigationHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Next or TopCommand.Prev or TopCommand.Goto
            or TopCommand.VimTop or TopCommand.VimMiddle or TopCommand.VimBottom
            or TopCommand.NextUnplayed or TopCommand.PrevUnplayed;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var nav = ctx.Cases.Navigation;
        switch (cmd.Kind)
        {
            case TopCommand.Next: nav.SelectRelative(+1); return;
            case TopCommand.Prev: nav.SelectRelative(-1); return;
            case TopCommand.Goto: nav.ExecGoto(cmd.Args); return;
            case TopCommand.VimTop:       nav.SelectAbsolute(0); return;
            case TopCommand.VimMiddle:    nav.SelectMiddle();    return;
            case TopCommand.VimBottom:    nav.SelectAbsolute(int.MaxValue); return;
            case TopCommand.NextUnplayed: nav.JumpUnplayed(+1); return;
            case TopCommand.PrevUnplayed: nav.JumpUnplayed(-1); return;
        }
    }
}
