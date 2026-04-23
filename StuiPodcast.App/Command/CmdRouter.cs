using StuiPodcast.App;
using StuiPodcast.App.Command;
using StuiPodcast.App.Command.Handler;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using StuiPodcast.Infra.Download;

#region public facade

// Entry point from ui → command pipeline. The top-level Handle() receives
// the CmdCases container (built once in Program.cs) and dispatches the
// parsed command to a registered handler. HandleQueue / HandleDownloads
// are kept as public fastpaths so the ui-side command loop can short-
// circuit queue/download verbs without going through the parser.
static class CmdRouter
{
    public static void Handle(
        string raw,
        IAudioPlayer audioPlayer,
        PlaybackCoordinator playback,
        IUiShell ui,
        MemoryLogSink mem,
        AppData data,
        Func<Task> persist,
        DownloadManager dlm,
        IEpisodeStore episodes,
        IFeedStore feedStore,
        IQueueService queue,
        CmdCases cases,
        Func<string, Task>? switchEngine = null,
        GpodderSyncService? syncService = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        // fastpaths
        if (cases.Queue.Handle(raw)) return;
        if (cases.Download.Handle(raw)) return;

        // fallback for :dl without sub-arg
        if (raw.StartsWith(":dl", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
        {
            var arg = raw.Contains(' ')
                ? raw[(raw.IndexOf(' ') + 1)..].Trim().ToLowerInvariant()
                : "";
            cases.Download.DlToggle(arg);
            cases.View.ApplyList();
            return;
        }

        var parsed = CmdParser.Parse(raw);
        if (parsed.Kind == TopCommand.Unknown) { ui.ShowOsd($"unknown: {parsed.Cmd}"); return; }

        var ctx = new CmdContext(audioPlayer, playback, ui, mem, data, persist, dlm, switchEngine,
            episodes, feedStore, queue, cases, syncService);

        // dispatch
        CommandDispatcher.Default.Dispatch(parsed, ctx);
    }

    public static bool HandleQueue(CmdCases cases, string cmd) => cases.Queue.Handle(cmd);
    public static bool HandleDownloads(CmdCases cases, string cmd) => cases.Download.Handle(cmd);
    public static void ApplyList(CmdCases cases) => cases.View.ApplyList();
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
    Search, Now, Jump, Theme,
    Sync
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
        new CmdSyncHandler(),
        // downloads & queue via public fastpaths
    });

    public CommandDispatcher(IEnumerable<ICmdHandler> handlers) => _handlers = handlers.ToList();

    public void Dispatch(CmdParsed cmd, CmdContext ctx)
    {
        var h = _handlers.FirstOrDefault(x => x.CanHandle(cmd.Kind));
        if (h == null) { ctx.Ui.ShowOsd($"unknown: {cmd.Cmd}"); return; }
        h.Handle(cmd, ctx);
    }
}
#endregion
