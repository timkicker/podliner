using StuiPodcast.App.Bootstrap;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdSystemHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Help or TopCommand.Quit or TopCommand.Logs or TopCommand.Osd
        or TopCommand.Write or TopCommand.WriteQuit or TopCommand.WriteQuitBang or TopCommand.QuitBang
        or TopCommand.Refresh or TopCommand.Undo;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var system = ctx.Cases.System;
        switch (cmd.Kind)
        {
            case TopCommand.Help: ctx.Ui.ShowKeysHelp(); return;
            case TopCommand.Quit: ctx.Ui.RequestQuit(); return;
            case TopCommand.Logs: system.ExecLogs(cmd.Args); return;
            case TopCommand.Osd:  system.ExecOsd(cmd.Args);  return;

            case TopCommand.QuitBang:
                try { Program.SkipSaveOnExit = true; } catch { }
                ctx.Ui.RequestQuit();
                return;

            case TopCommand.Write:         system.ExecWrite(); return;
            case TopCommand.WriteQuit:     system.ExecWriteQuit(bang: false); return;
            case TopCommand.WriteQuitBang: system.ExecWriteQuit(bang: true);  return;

            case TopCommand.Refresh:
                ctx.Ui.ShowOsd("Refreshing…", 600);
                ctx.Ui.RequestRefresh();
                return;

            case TopCommand.Undo: ctx.Cases.Undo.Exec(cmd.Args); return;
        }
    }
}
