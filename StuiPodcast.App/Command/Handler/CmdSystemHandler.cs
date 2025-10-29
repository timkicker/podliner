using StuiPodcast.App.Bootstrap;
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
            case TopCommand.Help: ctx.Ui.ShowKeysHelp(); return;
            case TopCommand.Quit: ctx.Ui.RequestQuit(); return;
            case TopCommand.Logs: CmdSystemModule.ExecLogs(cmd.Args, ctx.Ui); return;
            case TopCommand.Osd:  CmdSystemModule.ExecOsd(cmd.Args, ctx.Ui);  return;

            case TopCommand.QuitBang:
                try { Program.SkipSaveOnExit = true; } catch { }
                ctx.Ui.RequestQuit();
                return;

            case TopCommand.Write:         CmdSystemModule.ExecWrite(ctx.Persist, ctx.Ui); return;
            case TopCommand.WriteQuit:     CmdSystemModule.ExecWriteQuit(ctx.Persist, ctx.Ui, bang:false); return;
            case TopCommand.WriteQuitBang: CmdSystemModule.ExecWriteQuit(ctx.Persist, ctx.Ui, bang:true);  return;

            case TopCommand.Refresh:
                ctx.Ui.ShowOsd("Refreshing…", 600);
                ctx.Ui.RequestRefresh();
                return;
        }
    }
}
