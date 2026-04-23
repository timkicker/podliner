using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdPlaybackHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Toggle or TopCommand.Seek or TopCommand.Volume or TopCommand.Speed or TopCommand.Replay
            or TopCommand.Now or TopCommand.Jump or TopCommand.PlayNext or TopCommand.PlayPrev
            or TopCommand.Sleep or TopCommand.Chapter;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var p = ctx.AudioPlayer;
        var transport = ctx.Cases.Transport;
        var nav = ctx.Cases.Navigation;

        switch (cmd.Kind)
        {
            case TopCommand.Toggle:
                if ((p.Capabilities & PlayerCapabilities.Pause) == 0) { ctx.Ui.ShowOsd("pause not supported by current engine"); return; }
                p.TogglePause(); return;

            case TopCommand.Seek:   transport.ExecSeek(cmd.Args); return;
            case TopCommand.Volume: transport.ExecVolume(cmd.Args); return;
            case TopCommand.Speed:  transport.ExecSpeed(cmd.Args);  return;
            case TopCommand.Replay: transport.ExecReplay(cmd.Args); return;
            case TopCommand.Now:    transport.ExecNow(); return;
            case TopCommand.Jump:   transport.ExecJump(cmd.Args); return;

            case TopCommand.PlayNext: nav.SelectRelative(+1, playAfterSelect: true); return;
            case TopCommand.PlayPrev: nav.SelectRelative(-1, playAfterSelect: true); return;

            case TopCommand.Sleep:    ctx.Cases.Sleep.Exec(cmd.Args); return;
            case TopCommand.Chapter:  ctx.Cases.Chapters.Exec(cmd.Args); return;
        }
    }
}
