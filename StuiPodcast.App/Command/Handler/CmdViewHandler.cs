using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdViewHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Search or TopCommand.Sort or TopCommand.Filter or TopCommand.PlayerBar or TopCommand.Theme;
    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Search:    CmdViewModule.ExecSearch(cmd.Args, ui, data); return;
            case TopCommand.Sort:      CmdViewModule.ExecSort(cmd.Args, ui, data, ctx.Persist); CmdViewModule.ApplyList(ui, data); return;
            case TopCommand.Filter:    CmdViewModule.ExecFilter(cmd.Args, ui, data, ctx.Persist); CmdViewModule.ApplyList(ui, data); return;
            case TopCommand.PlayerBar: CmdViewModule.ExecPlayerBar(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.Theme:     CmdViewModule.ExecTheme(cmd.Args, ui, data, ctx.Persist); return;
        }
    }
}