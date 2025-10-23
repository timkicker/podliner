using StuiPodcast.App.Command;
using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class NetStateHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Net or TopCommand.PlaySource or TopCommand.Save;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Net:         NetModule.ExecNet(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.PlaySource:  NetModule.ExecPlaySource(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.Save:        StateModule.ExecSave(cmd.Args, ui, data, ctx.Persist); return;
        }
    }
}