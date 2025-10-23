using StuiPodcast.App.Command;
using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class SystemHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Help or TopCommand.Quit or TopCommand.Logs or TopCommand.Osd
        or TopCommand.Write or TopCommand.WriteQuit or TopCommand.WriteQuitBang or TopCommand.QuitBang
        or TopCommand.Refresh;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.Help: ctx.UI.ShowKeysHelp(); return;
            case TopCommand.Quit: ctx.UI.RequestQuit(); return;
            case TopCommand.Logs: SystemModule.ExecLogs(cmd.Args, ctx.UI); return;
            case TopCommand.Osd:  SystemModule.ExecOsd(cmd.Args, ctx.UI);  return;

            case TopCommand.QuitBang:
                try { Program.SkipSaveOnExit = true; } catch { }
                ctx.UI.RequestQuit();
                return;

            case TopCommand.Write:         SystemModule.ExecWrite(ctx.Persist, ctx.UI); return;
            case TopCommand.WriteQuit:     SystemModule.ExecWriteQuit(ctx.Persist, ctx.UI, bang:false); return;
            case TopCommand.WriteQuitBang: SystemModule.ExecWriteQuit(ctx.Persist, ctx.UI, bang:true);  return;

            case TopCommand.Refresh:
                ctx.UI.ShowOsd("Refreshing…", 600);
                ctx.UI.RequestRefresh();
                return;
        }
    }
}
