using StuiPodcast.App.Command.Module;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Handler;

internal sealed class CmdPlaybackHandler : ICmdHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Toggle or TopCommand.Seek or TopCommand.Volume or TopCommand.Speed or TopCommand.Replay
            or TopCommand.Now or TopCommand.Jump or TopCommand.PlayNext or TopCommand.PlayPrev;

    public void Handle(CmdParsed cmd, CmdContext ctx)
    {
        var p = ctx.AudioPlayer; var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Toggle:
                if ((p.Capabilities & PlayerCapabilities.Pause) == 0) { ui.ShowOsd("pause not supported by current engine"); return; }
                p.TogglePause(); return;

            case TopCommand.Seek:   CmdPlaybackModule.ExecSeek(cmd.Args, p, ui); return;
            case TopCommand.Volume: CmdPlaybackModule.ExecVolume(cmd.Args, p, data, ctx.Persist, ui); return;
            case TopCommand.Speed:  CmdPlaybackModule.ExecSpeed(cmd.Args, p, data, ctx.Persist, ui);  return;
            case TopCommand.Replay: CmdPlaybackModule.ExecReplay(cmd.Args, p, ui); return;
            case TopCommand.Now:    CmdPlaybackModule.ExecNow(ui, data); return;
            case TopCommand.Jump:   CmdPlaybackModule.ExecJump(cmd.Args, p, ui); return;

            case TopCommand.PlayNext:
                CmdNavigationModule.SelectRelative(+1, ui, data, playAfterSelect: true, ctx.Playback);
                return;
            case TopCommand.PlayPrev:
                CmdNavigationModule.SelectRelative(-1, ui, data, playAfterSelect: true, ctx.Playback);
                return;
        }
    }
}
