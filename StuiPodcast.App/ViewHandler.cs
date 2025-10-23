namespace StuiPodcast.App;

internal sealed class ViewHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Search or TopCommand.Sort or TopCommand.Filter or TopCommand.PlayerBar or TopCommand.Theme;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Search:    ViewModule.ExecSearch(cmd.Args, ui, data); return;
            case TopCommand.Sort:      ViewModule.ExecSort(cmd.Args, ui, data, ctx.Persist); ViewModule.ApplyList(ui, data); return;
            case TopCommand.Filter:    ViewModule.ExecFilter(cmd.Args, ui, data, ctx.Persist); ViewModule.ApplyList(ui, data); return;
            case TopCommand.PlayerBar: ViewModule.ExecPlayerBar(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.Theme:     ViewModule.ExecTheme(cmd.Args, ui, data, ctx.Persist); return;
        }
    }
}