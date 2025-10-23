using StuiPodcast.App.Command.Module;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdSystemHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Help or TopCommand.Quit or TopCommand.Logs or TopCommand.Osd
        or TopCommand.Write or TopCommand.WriteQuit or TopCommand.WriteQuitBang or TopCommand.QuitBang
        or TopCommand.Refresh;
    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.Help: ctx.UI.ShowKeysHelp(); return;
            case TopCommand.Quit: ctx.UI.RequestQuit(); return;
            case TopCommand.Logs: CmdSystemModule.ExecLogs(cmd.Args, ctx.UI); return;
            case TopCommand.Osd:  CmdSystemModule.ExecOsd(cmd.Args, ctx.UI);  return;

            case TopCommand.QuitBang:
                try { Program.SkipSaveOnExit = true; } catch { }
                ctx.UI.RequestQuit();
                return;

            case TopCommand.Write:         CmdSystemModule.ExecWrite(ctx.Persist, ctx.UI); return;
            case TopCommand.WriteQuit:     CmdSystemModule.ExecWriteQuit(ctx.Persist, ctx.UI, bang:false); return;
            case TopCommand.WriteQuitBang: CmdSystemModule.ExecWriteQuit(ctx.Persist, ctx.UI, bang:true);  return;

            case TopCommand.Refresh:
                ctx.UI.ShowOsd("Refreshing…", 600);
                ctx.UI.RequestRefresh();
                return;
        }
    }
}
