using StuiPodcast.App.Command;
using StuiPodcast.App.Command.Module;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Handler;

internal sealed class PlaybackHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Toggle or TopCommand.Seek or TopCommand.Volume or TopCommand.Speed or TopCommand.Replay
            or TopCommand.Now or TopCommand.Jump or TopCommand.PlayNext or TopCommand.PlayPrev;

    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        var p = ctx.Player; var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Toggle:
                if ((p.Capabilities & PlayerCapabilities.Pause) == 0) { ui.ShowOsd("pause not supported by current engine"); return; }
                p.TogglePause(); return;

            case TopCommand.Seek:   PlaybackModule.ExecSeek(cmd.Args, p, ui); return;
            case TopCommand.Volume: PlaybackModule.ExecVolume(cmd.Args, p, data, ctx.Persist, ui); return;
            case TopCommand.Speed:  PlaybackModule.ExecSpeed(cmd.Args, p, data, ctx.Persist, ui);  return;
            case TopCommand.Replay: PlaybackModule.ExecReplay(cmd.Args, p, ui); return;
            case TopCommand.Now:    PlaybackModule.ExecNow(ui, data); return;
            case TopCommand.Jump:   PlaybackModule.ExecJump(cmd.Args, p, ui); return;

            case TopCommand.PlayNext:
                NavigationModule.SelectRelative(+1, ui, data, playAfterSelect: true, ctx.Playback);
                return;
            case TopCommand.PlayPrev:
                NavigationModule.SelectRelative(-1, ui, data, playAfterSelect: true, ctx.Playback);
                return;
        }
    }
}
