using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using StuiPodcast.App;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Opml;
using StuiPodcast.Infra.Player;
using ThemeMode = StuiPodcast.App.UI.Shell.ThemeMode;

// ============================================================================
// Public Facade
// ============================================================================
static class CommandRouter
{
    // --------------------------- Public API (unchanged) ---------------------------

    public static void Handle(
        string raw,
        IPlayer player,
        PlaybackCoordinator playback,
        Shell ui,
        MemoryLogSink mem,
        AppData data,
        Func<Task> persist,
        DownloadManager dlm,
        Func<string, Task>? switchEngine = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        // Fastpaths (beibehalten, damit bestehende Aufrufer identisch bleiben)
        if (HandleQueue(raw, ui, data, persist)) return;
        if (HandleDownloads(raw, ui, data, dlm, persist)) return;

        // Fallback für :dl ohne Sub-Arg
        if (raw.StartsWith(":dl", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
        {
            var arg = raw.Contains(' ')
                ? raw[(raw.IndexOf(' ') + 1)..].Trim().ToLowerInvariant()
                : "";
            DownloadsModule.DlToggle(arg, ui, data, persist, dlm);
            ApplyList(ui, data);
            return;
        }

        var parsed = CommandParser.Parse(raw);
        if (parsed.Kind == TopCommand.Unknown) { ui.ShowOsd($"unknown: {parsed.Cmd}"); return; }

        var ctx = new CommandContext(player, playback, ui, mem, data, persist, dlm, switchEngine);

        // Dispatch
        CommandDispatcher.Default.Dispatch(parsed, ctx);
    }

    public static bool HandleQueue(string cmd, Shell ui, AppData data, Func<Task> saveAsync)
        => QueueModule.HandleQueue(cmd, ui, data, saveAsync);

    public static bool HandleDownloads(string cmd, Shell ui, AppData data, DownloadManager dlm, Func<Task> saveAsync)
        => DownloadsModule.HandleDownloads(cmd, ui, data, dlm, saveAsync);

    public static void ApplyList(Shell ui, AppData data)
        => ViewModule.ApplyList(ui, data);
}

// ============================================================================
// Core types (Parser, Context, Dispatcher, Enum)
// ============================================================================
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




internal sealed class CommandDispatcher
{
    private readonly List<ICommandHandler> _handlers;

    public static CommandDispatcher Default { get; } = new CommandDispatcher(new ICommandHandler[]
    {
        new SystemHandler(),
        new EngineHandler(),
        new PlaybackHandler(),
        new NavigationHandler(),
        new ViewHandler(),
        new NetStateHandler(),
        new FeedsHandler(),
        new HistoryHandler(),
        new OpmlHandler(),
        new IoHandler(),
        // Downloads & Queue bleiben über Public-Fastpaths erreichbar
    });

    public CommandDispatcher(IEnumerable<ICommandHandler> handlers) => _handlers = handlers.ToList();

    public void Dispatch(ParsedCommand cmd, CommandContext ctx)
    {
        var h = _handlers.FirstOrDefault(x => x.CanHandle(cmd.Kind));
        if (h == null) { ctx.UI.ShowOsd($"unknown: {cmd.Cmd}"); return; }
        h.Handle(cmd, ctx);
    }
}

