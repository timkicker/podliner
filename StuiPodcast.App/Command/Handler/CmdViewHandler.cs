namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdViewHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Search or TopCommand.Sort or TopCommand.Filter or TopCommand.PlayerBar or TopCommand.Theme;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var view = ctx.Cases.View;
        switch (cmd.Kind)
        {
            case TopCommand.Search:    view.ExecSearch(cmd.Args); return;
            case TopCommand.Sort:      view.ExecSort(cmd.Args); view.ApplyList(); return;
            case TopCommand.Filter:    view.ExecFilter(cmd.Args); view.ApplyList(); return;
            case TopCommand.PlayerBar: view.ExecPlayerBar(cmd.Args); return;
            case TopCommand.Theme:     view.ExecTheme(cmd.Args); return;
        }
    }
}
