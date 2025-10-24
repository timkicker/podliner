using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using StuiPodcast.App;
using StuiPodcast.App.Command;
using StuiPodcast.App.Command.Handler;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Opml;
using StuiPodcast.Infra.Player;
using ThemeMode = StuiPodcast.App.UI.UiShell.ThemeMode;
using StuiPodcast.App.Command.Module;
using StuiPodcast.Infra.Download;

#region public facade
static class CmdRouter
{
 
    public static void Handle(
        string raw,
        IAudioPlayer audioPlayer,
        PlaybackCoordinator playback,
        UiShell ui,
        MemoryLogSink mem,
        AppData data,
        Func<Task> persist,
        DownloadManager dlm,
        Func<string, Task>? switchEngine = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        // fastpaths
        if (HandleQueue(raw, ui, data, persist)) return;
        if (HandleDownloads(raw, ui, data, dlm, persist)) return;

        // fallback for :dl without sub-arg
        if (raw.StartsWith(":dl", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
        {
            var arg = raw.Contains(' ')
                ? raw[(raw.IndexOf(' ') + 1)..].Trim().ToLowerInvariant()
                : "";
            CmdDownloadsModule.DlToggle(arg, ui, data, persist, dlm);
            ApplyList(ui, data);
            return;
        }

        var parsed = CmdParser.Parse(raw);
        if (parsed.Kind == TopCommand.Unknown) { ui.ShowOsd($"unknown: {parsed.Cmd}"); return; }

        var ctx = new CmdContext(audioPlayer, playback, ui, mem, data, persist, dlm, switchEngine);

        // dispatch
        CommandDispatcher.Default.Dispatch(parsed, ctx);
    }

    public static bool HandleQueue(string cmd, UiShell ui, AppData data, Func<Task> saveAsync)
        => CmdQueueModule.HandleQueue(cmd, ui, data, saveAsync);

    public static bool HandleDownloads(string cmd, UiShell ui, AppData data, DownloadManager dlm, Func<Task> saveAsync)
        => CmdDownloadsModule.HandleDownloads(cmd, ui, data, dlm, saveAsync);

    public static void ApplyList(UiShell ui, AppData data)
        => CmdViewModule.ApplyList(ui, data);
}
#endregion

#region core types
internal enum TopCommand
{
    Unknown, Engine,
    Help, Quit, Logs, Osd,
    Toggle, Seek, Volume, Speed, Replay,
    Next, Prev, PlayNext, PlayPrev,
    Goto, VimTop, VimMiddle, VimBottom,
    NextUnplayed, PrevUnplayed,
    Save, Sort, Filter, PlayerBar,
    Net, PlaySource,
    AddFeed, Refresh, RemoveFeed, Feed,
    History, Opml, Open, Copy,
    Write, WriteQuit, WriteQuitBang, QuitBang,
    Search, Now, Jump, Theme
}
#endregion

#region dispatcher
internal sealed class CommandDispatcher
{
    private readonly List<ICmdHandler> _handlers;

    public static CommandDispatcher Default { get; } = new CommandDispatcher(new ICmdHandler[]
    {
        new CmdSystemHandler(),
        new CmdEngineHandler(),
        new CmdPlaybackHandler(),
        new CmdNavigationHandler(),
        new CmdViewHandler(),
        new CmdNetStateHandler(),
        new CmdFeedsHandler(),
        new CmdHistoryHandler(),
        new CmdOpmlHandler(),
        new CmdIoHandler(),
        // downloads & queue via public fastpaths
    });

    public CommandDispatcher(IEnumerable<ICmdHandler> handlers) => _handlers = handlers.ToList();

    public void Dispatch(CmdParsed cmd, CmdContext ctx)
    {
        var h = _handlers.FirstOrDefault(x => x.CanHandle(cmd.Kind));
        if (h == null) { ctx.UI.ShowOsd($"unknown: {cmd.Cmd}"); return; }
        h.Handle(cmd, ctx);
    }
}
#endregion
