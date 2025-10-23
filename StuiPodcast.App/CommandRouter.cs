using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
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

internal sealed class ParsedCommand
{
    public string Raw { get; }
    public string Cmd { get; }
    public string[] Args { get; }
    public TopCommand Kind { get; }
    public ParsedCommand(string raw, string cmd, string[] args, TopCommand kind)
    { Raw = raw; Cmd = cmd; Args = args; Kind = kind; }
}

internal static class CommandParser
{
    public static ParsedCommand Parse(string raw)
    {
        var tokens = Tokenize(raw.Trim());
        if (tokens.Length == 0) return new ParsedCommand(raw, "", Array.Empty<string>(), TopCommand.Unknown);
        var (cmd, args) = SplitCmd(tokens);
        return new ParsedCommand(raw, cmd, args, MapTop(cmd));
    }

    // --- tokenizer/mapper (aus dem Altcode)
    private static string[] Tokenize(string raw)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        char quoteChar = '\0';

        for (int i = 0; i < raw.Length; i++)
        {
            char ch = raw[i];

            if (ch == '\\' && i + 1 < raw.Length) { i++; cur.Append(raw[i]); continue; }

            if (!inQuotes && (ch == '"' || ch == '\''))
            { inQuotes = true; quoteChar = ch; continue; }

            if (inQuotes && ch == quoteChar)
            { inQuotes = false; quoteChar = '\0'; continue; }

            if (!inQuotes && char.IsWhiteSpace(ch))
            { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } continue; }

            cur.Append(ch);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list.ToArray();
    }

    private static (string cmd, string[] args) SplitCmd(string[] tokens)
    {
        if (tokens.Length == 0) return ("", Array.Empty<string>());
        var cmd = tokens[0];
        var args = tokens.Skip(1).ToArray();
        return (cmd, args);
    }

    private static TopCommand MapTop(string cmd)
    {
        if (cmd.StartsWith(":engine", StringComparison.OrdinalIgnoreCase)) return TopCommand.Engine;
        if (cmd.StartsWith(":opml", StringComparison.OrdinalIgnoreCase)) return TopCommand.Opml;

        // Aliases
        if (cmd.Equals(":a", StringComparison.OrdinalIgnoreCase)) return TopCommand.AddFeed;
        if (cmd.Equals(":r", StringComparison.OrdinalIgnoreCase)) return TopCommand.Refresh;

        // Neue Commands
        if (cmd.StartsWith(":open", StringComparison.OrdinalIgnoreCase)) return TopCommand.Open;
        if (cmd.StartsWith(":copy", StringComparison.OrdinalIgnoreCase)) return TopCommand.Copy;

        cmd = cmd?.Trim() ?? "";
        if (cmd.Equals(":h", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":help", StringComparison.OrdinalIgnoreCase)) return TopCommand.Help;
        if (cmd.Equals(":q", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":quit", StringComparison.OrdinalIgnoreCase)) return TopCommand.Quit;
        if (cmd.Equals(":q!", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":quit!", StringComparison.OrdinalIgnoreCase)) return TopCommand.QuitBang;

        // Vim writes
        if (cmd.Equals(":w", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":write", StringComparison.OrdinalIgnoreCase)) return TopCommand.Write;
        if (cmd.Equals(":wq", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":x", StringComparison.OrdinalIgnoreCase)) return TopCommand.WriteQuit;
        if (cmd.Equals(":wq!", StringComparison.OrdinalIgnoreCase)) return TopCommand.WriteQuitBang;

        // Neu
        if (cmd.StartsWith(":search", StringComparison.OrdinalIgnoreCase)) return TopCommand.Search;
        if (cmd.Equals(":now", StringComparison.OrdinalIgnoreCase)) return TopCommand.Now;
        if (cmd.StartsWith(":jump", StringComparison.OrdinalIgnoreCase)) return TopCommand.Jump;
        if (cmd.StartsWith(":theme", StringComparison.OrdinalIgnoreCase)) return TopCommand.Theme;

        if (cmd.StartsWith(":logs", StringComparison.OrdinalIgnoreCase)) return TopCommand.Logs;
        if (cmd.StartsWith(":osd", StringComparison.OrdinalIgnoreCase)) return TopCommand.Osd;

        if (cmd.Equals(":toggle", StringComparison.OrdinalIgnoreCase)) return TopCommand.Toggle;
        if (cmd.StartsWith(":seek", StringComparison.OrdinalIgnoreCase)) return TopCommand.Seek;
        if (cmd.StartsWith(":vol", StringComparison.OrdinalIgnoreCase)) return TopCommand.Volume;
        if (cmd.StartsWith(":speed", StringComparison.OrdinalIgnoreCase)) return TopCommand.Speed;
        if (cmd.StartsWith(":replay", StringComparison.OrdinalIgnoreCase)) return TopCommand.Replay;

        if (cmd.Equals(":next", StringComparison.OrdinalIgnoreCase)) return TopCommand.Next;
        if (cmd.Equals(":prev", StringComparison.OrdinalIgnoreCase)) return TopCommand.Prev;
        if (cmd.Equals(":play-next", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlayNext;
        if (cmd.Equals(":play-prev", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlayPrev;

        if (cmd.StartsWith(":goto", StringComparison.OrdinalIgnoreCase)) return TopCommand.Goto;
        if (cmd.Equals(":zt", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":H", StringComparison.OrdinalIgnoreCase)) return TopCommand.VimTop;
        if (cmd.Equals(":zz", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":M", StringComparison.OrdinalIgnoreCase)) return TopCommand.VimMiddle;
        if (cmd.Equals(":zb", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":L", StringComparison.OrdinalIgnoreCase)) return TopCommand.VimBottom;
        if (cmd.Equals(":next-unplayed", StringComparison.OrdinalIgnoreCase)) return TopCommand.NextUnplayed;
        if (cmd.Equals(":prev-unplayed", StringComparison.OrdinalIgnoreCase)) return TopCommand.PrevUnplayed;

        if (cmd.StartsWith(":save", StringComparison.OrdinalIgnoreCase)) return TopCommand.Save;
        if (cmd.StartsWith(":sort", StringComparison.OrdinalIgnoreCase)) return TopCommand.Sort;
        if (cmd.StartsWith(":filter", StringComparison.OrdinalIgnoreCase)) return TopCommand.Filter;
        if (cmd.StartsWith(":player", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlayerBar;

        if (cmd.StartsWith(":net", StringComparison.OrdinalIgnoreCase)) return TopCommand.Net;
        if (cmd.StartsWith(":play-source", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlaySource;

        if (cmd.StartsWith(":add", StringComparison.OrdinalIgnoreCase)) return TopCommand.AddFeed;
        if (cmd.StartsWith(":refresh", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith(":update", StringComparison.OrdinalIgnoreCase)) return TopCommand.Refresh;
        if (cmd.Equals(":rm-feed", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":remove-feed", StringComparison.OrdinalIgnoreCase)) return TopCommand.RemoveFeed;
        if (cmd.StartsWith(":feed", StringComparison.OrdinalIgnoreCase)) return TopCommand.Feed;

        if (cmd.StartsWith(":history", StringComparison.OrdinalIgnoreCase)) return TopCommand.History;

        return TopCommand.Unknown;
    }
}

internal sealed class CommandContext
{
    public IPlayer Player { get; }
    public PlaybackCoordinator Playback { get; }
    public Shell UI { get; }
    public MemoryLogSink Mem { get; }
    public AppData Data { get; }
    public Func<Task> Persist { get; }
    public DownloadManager Dlm { get; }
    public Func<string, Task>? SwitchEngine { get; }

    public CommandContext(IPlayer player, PlaybackCoordinator playback, Shell ui, MemoryLogSink mem,
                          AppData data, Func<Task> persist, DownloadManager dlm, Func<string, Task>? switchEngine)
    {
        Player = player; Playback = playback; UI = ui; Mem = mem;
        Data = data; Persist = persist; Dlm = dlm; SwitchEngine = switchEngine;
    }
}

internal interface ICommandHandler
{
    bool CanHandle(TopCommand kind);
    void Handle(ParsedCommand cmd, CommandContext ctx);
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

// ============================================================================
// Modules (Handlers) – kapseln die eigentliche Logik
// ============================================================================

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

internal sealed class EngineHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Engine;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
        => EngineModule.ExecEngine(cmd.Args, ctx.Player, ctx.UI, ctx.Data, ctx.Persist, ctx.SwitchEngine);
}

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

internal sealed class NavigationHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Next or TopCommand.Prev or TopCommand.Goto
         or TopCommand.VimTop or TopCommand.VimMiddle or TopCommand.VimBottom
         or TopCommand.NextUnplayed or TopCommand.PrevUnplayed;

    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Next: NavigationModule.SelectRelative(+1, ui, data); return;
            case TopCommand.Prev: NavigationModule.SelectRelative(-1, ui, data); return;
            case TopCommand.Goto: NavigationModule.ExecGoto(cmd.Args, ui, data); return;
            case TopCommand.VimTop:    NavigationModule.SelectAbsolute(0, ui, data); return;
            case TopCommand.VimMiddle: NavigationModule.SelectMiddle(ui, data);     return;
            case TopCommand.VimBottom: NavigationModule.SelectAbsolute(int.MaxValue, ui, data); return;
            case TopCommand.NextUnplayed: NavigationModule.JumpUnplayed(+1, ui, ctx.Playback, data); return;
            case TopCommand.PrevUnplayed: NavigationModule.JumpUnplayed(-1, ui, ctx.Playback, data); return;
        }
    }
}

internal sealed class ViewHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) =>
        k is TopCommand.Search or TopCommand.Sort or TopCommand.Filter or TopCommand.PlayerBar or TopCommand.Theme;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Search:    ViewModule.ExecSearch(cmd.Args, ui, data); return;
            case TopCommand.Sort:      ViewModule.ExecSort(cmd.Args, ui, data, ctx.Persist); ViewModule.ApplyList(ui, data); return;
            case TopCommand.Filter:    ViewModule.ExecFilter(cmd.Args, ui, data, ctx.Persist); ViewModule.ApplyList(ui, data); return;
            case TopCommand.PlayerBar: ViewModule.ExecPlayerBar(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.Theme:     ViewModule.ExecTheme(cmd.Args, ui, data, ctx.Persist); return;
        }
    }
}

internal sealed class NetStateHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Net or TopCommand.PlaySource or TopCommand.Save;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        var ui = ctx.UI; var data = ctx.Data;
        switch (cmd.Kind)
        {
            case TopCommand.Net:         NetModule.ExecNet(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.PlaySource:  NetModule.ExecPlaySource(cmd.Args, ui, data, ctx.Persist); return;
            case TopCommand.Save:        StateModule.ExecSave(cmd.Args, ui, data, ctx.Persist); return;
        }
    }
}

internal sealed class FeedsHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.AddFeed or TopCommand.Feed or TopCommand.RemoveFeed;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.AddFeed:   FeedsModule.ExecAddFeed(cmd.Args, ctx.UI); return;
            case TopCommand.Feed:      FeedsModule.ExecFeed(cmd.Args, ctx.UI, ctx.Data, ctx.Persist); return;
            case TopCommand.RemoveFeed: FeedsModule.RemoveSelectedFeed(ctx.UI, ctx.Data, ctx.Persist); return;
        }
    }
}

internal sealed class HistoryHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.History;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
        => HistoryModule.ExecHistory(cmd.Args, ctx.UI, ctx.Data, ctx.Persist);
}

internal sealed class OpmlHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k == TopCommand.Opml;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
        => OpmlModule.ExecOpml(cmd.Args, ctx.UI, ctx.Data, ctx.Persist);
}

internal sealed class IoHandler : ICommandHandler
{
    public bool CanHandle(TopCommand k) => k is TopCommand.Open or TopCommand.Copy;
    public void Handle(ParsedCommand cmd, CommandContext ctx)
    {
        switch (cmd.Kind)
        {
            case TopCommand.Open: IoModule.ExecOpen(cmd.Args, ctx.UI, ctx.Data); return;
            case TopCommand.Copy: IoModule.ExecCopy(cmd.Args, ctx.UI, ctx.Data); return;
        }
    }
}

// ============================================================================
// Module Implementations (extrahierte Logik aus dem Altcode)
// ============================================================================

internal static class SystemModule
{
    public static void ExecLogs(string[] args, Shell ui)
    {
        var a = args.Length > 0 ? args[0] : "";
        int tail = 500;
        if (int.TryParse(a, out var n) && n > 0) tail = Math.Min(n, 5000);
        ui.ShowLogsOverlay(tail);
    }

    public static void ExecOsd(string[] args, Shell ui)
    {
        var text = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(text)) ui.ShowOsd(text);
        else ui.ShowOsd("usage: :osd <text>");
    }

    public static void ExecWrite(Func<Task> persist, Shell ui)
    {
        try { persist().GetAwaiter().GetResult(); ui.ShowOsd("saved", 900); }
        catch (Exception ex) { ui.ShowOsd($"save failed: {ex.Message}", 1800); }
    }

    public static void ExecWriteQuit(Func<Task> persist, Shell ui, bool bang)
    {
        ui.ShowOsd(bang ? "saving… quitting!" : "saving… quitting…", 800);
        _ = Task.Run(async () =>
        {
            try { await persist().ConfigureAwait(false); }
            catch
            {
                Terminal.Gui.Application.MainLoop?.Invoke(() => ui.ShowOsd("save failed – aborting :wq", 1800));
                return;
            }
            Terminal.Gui.Application.MainLoop?.Invoke(() => ui.RequestQuit());
        });
    }
}

internal static class EngineModule
{
    public static void ExecEngine(
        string[] args,
        IPlayer player,
        Shell ui,
        AppData data,
        Func<Task> persist,
        Func<string, Task>? switchEngine = null)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (args.Length > 0 && string.Equals(args[0], "diag", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var activeName = player?.Name;
                if (string.IsNullOrWhiteSpace(activeName))
                    activeName = player?.GetType().Name ?? "none";

                var caps = player?.Capabilities ?? 0;
                bool cSeek = (caps & PlayerCapabilities.Seek) != 0;
                bool cPause = (caps & PlayerCapabilities.Pause) != 0;
                bool cSpeed = (caps & PlayerCapabilities.Speed) != 0;
                bool cVolume = (caps & PlayerCapabilities.Volume) != 0;

                var pref = string.IsNullOrWhiteSpace(data?.PreferredEngine) ? "auto" : data!.PreferredEngine!;
                var last = data?.LastEngineUsed ?? "";

                ui.ShowOsd($"engine: {activeName}  caps: seek={cSeek} pause={cPause} speed={cSpeed} vol={cVolume}  pref={pref} last={last}", 3000);
            }
            catch (Exception ex) { ui.ShowOsd($"engine: diag error ({ex.Message})", 2000); }
            return;
        }

        if (string.IsNullOrEmpty(arg) || arg == "show")
        {
            var caps = player.Capabilities;
            var txt = $"engine active: {player.Name}\n" +
                      $"preference: {data.PreferredEngine ?? "auto"}\n" +
                      "supports: " +
                      $"{((caps & PlayerCapabilities.Seek) != 0 ? "seek " : "")}" +
                      $"{((caps & PlayerCapabilities.Speed) != 0 ? "speed " : "")}" +
                      $"{((caps & PlayerCapabilities.Volume) != 0 ? "volume " : "")}".Trim();
            ui.ShowOsd(txt, 1500);
            return;
        }

        if (arg == "help")
        {
            try
            {
                var dlg = new Terminal.Gui.Dialog("Engine Help", 80, 24);
                var tv = new Terminal.Gui.TextView { ReadOnly = true, WordWrap = true, X = 0, Y = 0, Width = Terminal.Gui.Dim.Fill(), Height = Terminal.Gui.Dim.Fill() };
                tv.Text = StuiPodcast.App.HelpCatalog.EngineDoc;
                dlg.Add(tv);
                var ok = new Terminal.Gui.Button("OK", is_default: true);
                ok.Clicked += () => Terminal.Gui.Application.RequestStop();
                dlg.AddButton(ok);
                Terminal.Gui.Application.Run(dlg);
            }
            catch { }
            return;
        }

        if (arg is "auto" or "vlc" or "mpv" or "ffplay")
        {
            data.PreferredEngine = arg;
            _ = persist();

            if (switchEngine != null)
            {
                ui.ShowOsd($"engine: switching to {arg}…", 900);
                _ = switchEngine(arg); // fire-and-forget
            }
            else
            {
                ui.ShowOsd($"engine pref: {arg} (active: {player.Name})", 1500);
            }
            return;
        }

        ui.ShowOsd("usage: :engine [show|help|auto|vlc|mpv|ffplay]", 1500);
    }
}

internal static class PlaybackModule
{
    public static void ExecSeek(string[] args, IPlayer player, Shell ui)
    {
        if ((player.Capabilities & PlayerCapabilities.Seek) == 0) { ui.ShowOsd("seek not supported by current engine"); return; }
        if (string.Equals(player.Name, "ffplay", StringComparison.OrdinalIgnoreCase)) ui.ShowOsd("coarse seek (ffplay): restarts stream", 1100);
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Seek(arg, player);
    }

    public static void ExecVolume(string[] args, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Volume(arg, player, data, persist, ui);
    }

    public static void ExecSpeed(string[] args, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Speed(arg, player, data, persist, ui);
    }

    public static void ExecReplay(string[] args, IPlayer player, Shell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Replay(arg, player, ui);
    }

    public static void ExecNow(Shell ui, AppData data)
    {
        var nowId = ui.GetNowPlayingId();
        if (nowId == null) { ui.ShowOsd("no episode playing"); return; }
        var list = ListBuilder.BuildCurrentList(ui, data);
        var idx = list.FindIndex(e => e.Id == nowId);
        if (idx < 0) { ui.ShowOsd("playing episode not in current view"); return; }
        ui.SelectEpisodeIndex(idx);
        ui.ShowOsd("jumped to now", 700);
    }

    public static void ExecJump(string[] args, IPlayer player, Shell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (string.IsNullOrEmpty(arg)) { ui.ShowOsd("usage: :jump <hh:mm[:ss]|+/-sec|%>"); return; }
        Seek(arg, player);
    }

    // --- helpers copied
    public static void Replay(string arg, IPlayer player, Shell ui)
    {
        if (string.IsNullOrWhiteSpace(arg)) { player.SeekTo(TimeSpan.Zero); return; }
        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec > 0)
            player.SeekRelative(TimeSpan.FromSeconds(-sec));
        else
            player.SeekTo(TimeSpan.Zero);
    }

    public static void Seek(string arg, IPlayer player)
    {
        if ((player.Capabilities & PlayerCapabilities.Seek) == 0) return;
        if (string.IsNullOrWhiteSpace(arg)) return;

        var s = player.State;
        var len = s.Length ?? TimeSpan.Zero;

        if (arg.EndsWith("%", StringComparison.Ordinal) &&
            double.TryParse(arg.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            if (len > TimeSpan.Zero)
            {
                var ms = Math.Clamp(pct / 100.0, 0, 1) * len.TotalMilliseconds;
                player.SeekTo(TimeSpan.FromMilliseconds(ms));
            }
            return;
        }

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relSecs))
        {
            player.SeekRelative(TimeSpan.FromSeconds(relSecs));
            return;
        }

        var parts = arg.Split(':');
        if (parts.Length is 2 or 3)
        {
            int hh = 0, mm = 0, ss = 0;
            if (parts.Length == 3) { int.TryParse(parts[0], out hh); int.TryParse(parts[1], out mm); int.TryParse(parts[2], out ss); }
            else { int.TryParse(parts[0], out mm); int.TryParse(parts[1], out ss); }
            var total = hh * 3600 + mm * 60 + ss;
            player.SeekTo(TimeSpan.FromSeconds(Math.Max(0, total)));
            return;
        }

        if (int.TryParse(arg, out var absSecs))
            player.SeekTo(TimeSpan.FromSeconds(absSecs));
    }

    public static void Volume(string arg, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        if ((player.Capabilities & PlayerCapabilities.Volume) == 0) { ui.ShowOsd("volume not supported on this engine"); return; }
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Volume0_100;

        if ((arg.StartsWith("+") || arg.StartsWith("-")) && int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            var v = Math.Clamp(cur + delta, 0, 100);
            player.SetVolume(v); data.Volume0_100 = v; _ = persist(); ui.ShowOsd($"Vol {v}%"); return;
        }
        if (int.TryParse(arg, out var abs))
        { var v = Math.Clamp(abs, 0, 100); player.SetVolume(v); data.Volume0_100 = v; _ = persist(); ui.ShowOsd($"Vol {v}%"); }
    }

    public static void Speed(string arg, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        if ((player.Capabilities & PlayerCapabilities.Speed) == 0) { ui.ShowOsd("speed not supported on this engine"); return; }
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Speed;

        arg = arg.Replace(',', '.');

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
        {
            var s2 = Math.Clamp(cur + delta, 0.25, 3.0);
            player.SetSpeed(s2); data.Speed = s2; _ = persist(); ui.ShowOsd($"Speed {s2:0.0}×"); return;
        }
        if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var abs))
        {
            var s2 = Math.Clamp(abs, 0.25, 3.0);
            player.SetSpeed(s2); data.Speed = s2; _ = persist(); ui.ShowOsd($"Speed {s2:0.0}×");
        }
    }
}

internal static class NavigationModule
{
    public static void ExecGoto(string[] args, Shell ui, AppData data)
    {
        var arg = (args.Length > 0 ? args[0] : "").ToLowerInvariant();
        if (arg is "top" or "start") { SelectAbsolute(0, ui, data); return; }
        if (arg is "bottom" or "end") { SelectAbsolute(int.MaxValue, ui, data); return; }
    }

    public static void SelectRelative(int dir, Shell ui, AppData data, bool playAfterSelect = false, PlaybackCoordinator? playback = null)
    {
        var list = ListBuilder.BuildCurrentList(ui, data);
        if (list.Count == 0) return;

        var cur = ui.GetSelectedEpisode();
        int idx = 0;
        if (cur != null)
        {
            var i = list.FindIndex(x => x.Id == cur.Id);
            idx = i >= 0 ? i : 0;
        }

        int target = dir > 0 ? Math.Min(idx + 1, list.Count - 1) : Math.Max(idx - 1, 0);
        ui.SelectEpisodeIndex(target);

        if (playAfterSelect && playback != null)
        {
            var ep = list[target];
            playback.Play(ep);
            ui.SetWindowTitle(ep.Title);
            ui.ShowDetails(ep);
            ui.SetNowPlaying(ep.Id);
        }
    }

    public static void SelectAbsolute(int index, Shell ui, AppData data)
    {
        var list = ListBuilder.BuildCurrentList(ui, data);
        if (list.Count == 0) return;
        int target = Math.Clamp(index, 0, list.Count - 1);
        ui.SelectEpisodeIndex(target);
    }

    public static void SelectMiddle(Shell ui, AppData data)
    {
        var list = ListBuilder.BuildCurrentList(ui, data);
        if (list.Count == 0) return;
        int target = list.Count / 2;
        ui.SelectEpisodeIndex(target);
    }

    public static void JumpUnplayed(int dir, Shell ui, PlaybackCoordinator playback, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        IEnumerable<Episode> baseList = data.Episodes;

        if (feedId == VirtualFeeds.Saved) baseList = baseList.Where(e => e.Saved);
        else if (feedId == VirtualFeeds.Downloaded) baseList = baseList.Where(e => Program.IsDownloaded(e.Id));
        else if (feedId == VirtualFeeds.History) baseList = baseList.Where(e => e.Progress.LastPlayedAt != null);
        else if (feedId != VirtualFeeds.All) baseList = baseList.Where(e => e.FeedId == feedId);

        List<Episode> eps =
            (feedId == VirtualFeeds.History)
            ? baseList.OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
                      .ThenByDescending(e => e.Progress.LastPosMs).ToList()
            : baseList.OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue).ToList();

        if (eps.Count == 0) return;

        var cur = ui.GetSelectedEpisode();
        var startIdx = cur is null ? -1 : eps.FindIndex(x => ReferenceEquals(x, cur) || x.Id == cur.Id);
        int i = startIdx;

        for (int step = 0; step < eps.Count; step++)
        {
            i = dir > 0 ? (i + 1 + eps.Count) % eps.Count : (i - 1 + eps.Count) % eps.Count;
            if (!eps[i].ManuallyMarkedPlayed)
            {
                var target = eps[i];
                playback.Play(target);
                ui.SetWindowTitle(target.Title);
                ui.ShowDetails(target);
                ui.SetNowPlaying(target.Id);
                return;
            }
        }
    }
}

internal static class ViewModule
{
    public static void ApplyList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        IEnumerable<Episode> list = data.Episodes;
        if (data.UnplayedOnly) list = list.Where(e => !e.ManuallyMarkedPlayed);
        if (feedId is Guid fid) ui.SetEpisodesForFeed(fid, list);
    }

    public static void ExecSearch(string[] args, Shell ui, AppData data)
    {
        var query = string.Join(' ', args ?? Array.Empty<string>()).Trim();

        if (string.Equals(query, "clear", StringComparison.OrdinalIgnoreCase))
        {
            var fid = ui.GetSelectedFeedId();
            if (fid != null) ui.SetEpisodesForFeed(fid.Value, data.Episodes);
            ui.ShowOsd("search cleared", 800);
            return;
        }

        var feedId = ui.GetSelectedFeedId();
        var list = data.Episodes.AsEnumerable();

        if (feedId != null) list = list.Where(e => e.FeedId == feedId.Value);

        if (!string.IsNullOrWhiteSpace(query))
        {
            list = list.Where(e =>
                (e.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (feedId != null) ui.SetEpisodesForFeed(feedId.Value, list);
        ui.ShowOsd($"search: {query}", 900);
    }

    public static void ExecSort(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        HandleSort(arg, ui, data, persist);
    }

    public static void ExecFilter(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg is "unplayed" or "only") data.UnplayedOnly = true;
        else if (arg is "all") data.UnplayedOnly = false;
        else if (arg is "" or "toggle") data.UnplayedOnly = !data.UnplayedOnly;
        else { ui.ShowOsd("usage: :filter unplayed|all|toggle"); return; }

        ui.SetUnplayedFilterVisual(data.UnplayedOnly);
        _ = persist();
    }

    public static void ExecPlayerBar(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "toggle")
        { ui.TogglePlayerPlacement(); data.PlayerAtTop = !data.PlayerAtTop; _ = persist(); return; }
        if (arg == "top")
        { ui.SetPlayerPlacement(true); data.PlayerAtTop = true; _ = persist(); return; }
        if (arg is "bottom" or "bot")
        { ui.SetPlayerPlacement(false); data.PlayerAtTop = false; _ = persist(); return; }
    }

    public static void ExecTheme(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "toggle")
        {
            ui.ToggleTheme();
            data.ThemePref = null;
            _ = persist();
            return;
        }

        ThemeMode mode = arg switch
        {
            "base"   => ThemeMode.Base,
            "accent" => ThemeMode.MenuAccent,
            "native" => ThemeMode.Native,
            "auto"   => (OperatingSystem.IsWindows() ? ThemeMode.Base : ThemeMode.MenuAccent),
            _        => (OperatingSystem.IsWindows() ? ThemeMode.Base : ThemeMode.MenuAccent)
        };

        try { ui.SetTheme(mode); data.ThemePref = mode.ToString(); _ = persist(); ui.ShowOsd($"theme: {mode}"); }
        catch { ui.ShowOsd("theme: failed"); }
    }

    // --- sort helper (from old)
    private static void HandleSort(string arg, Shell ui, AppData data, Func<Task> persist)
    {
        if (arg.Equals("show", StringComparison.OrdinalIgnoreCase)) { ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}"); return; }
        if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        { data.SortBy = "pubdate"; data.SortDir = "desc"; _ = persist(); ui.ShowOsd("sort: pubdate desc"); return; }

        if (arg.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        { data.SortDir = (data.SortDir?.Equals("desc", StringComparison.OrdinalIgnoreCase) == true) ? "asc" : "desc"; _ = persist(); ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}"); return; }

        string[] keys = new[] { "pubdate", "title", "played", "progress", "feed" };
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 1 && parts[0].Equals("by", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                var key = parts[1].ToLowerInvariant();
                if (!keys.Contains(key)) { ui.ShowOsd("sort: invalid key"); return; }
                data.SortBy = key;

                if (parts.Length >= 3)
                {
                    var dir = parts[2].ToLowerInvariant();
                    if (dir is "asc" or "desc") data.SortDir = dir;
                }

                _ = persist();
                ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}");
                return;
            }
        }

        ui.ShowOsd("sort: by pubdate|title|played|progress|feed [asc|desc]");
    }
}

internal static class NetModule
{
    public static void ExecNet(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "online" or "on") { data.NetworkOnline = true; _ = persist(); ui.ShowOsd("Online", 600); }
        else if (arg is "offline" or "off") { data.NetworkOnline = false; _ = persist(); ui.ShowOsd("Offline", 600); }
        else if (string.IsNullOrEmpty(arg) || arg == "toggle") { data.NetworkOnline = !data.NetworkOnline; _ = persist(); ui.ShowOsd(data.NetworkOnline ? "Online" : "Offline", 600); }
        else { ui.ShowOsd("usage: :net online|offline|toggle", 1200); }

        ViewModule.ApplyList(ui, data);
        ui.RefreshEpisodesForSelectedFeed(data.Episodes);

        var nowId = ui.GetNowPlayingId();
        if (nowId != null)
        {
            var playing = data.Episodes.FirstOrDefault(x => x.Id == nowId);
            if (playing != null)
                ui.SetWindowTitle((!data.NetworkOnline ? "[OFFLINE] " : "") + (playing.Title ?? "—"));
        }
    }

    public static void ExecPlaySource(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "show" or "") { ui.ShowOsd($"play-source: {data.PlaySource ?? "auto"}"); return; }

        if (arg is "auto" or "local" or "remote") { data.PlaySource = arg; _ = persist(); ui.ShowOsd($"play-source: {arg}"); }
        else ui.ShowOsd("usage: :play-source auto|local|remote|show");
    }
}

internal static class StateModule
{
    public static void ExecSave(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        SaveToggle(arg, ui, data, persist);
    }

    private static void SaveToggle(string arg, Shell ui, AppData data, Func<Task> persist)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool newVal = ep.Saved;

        if (arg is "on" or "true" or "+") newVal = true;
        else if (arg is "off" or "false" or "-") newVal = false;
        else newVal = !ep.Saved;

        ep.Saved = newVal;
        _ = persist();

        ViewModule.ApplyList(ui, data);
        ui.ShowOsd(newVal ? "Saved ★" : "Unsaved");
    }
}

internal static class FeedsModule
{
    public static void ExecAddFeed(string[] args, Shell ui)
    {
        var url = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(url)) ui.RequestAddFeed(url);
        else ui.ShowOsd("usage: :add <rss-url>");
    }

    public static void ExecFeed(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        Guid? target = arg switch
        {
            "all"        => VirtualFeeds.All,
            "saved"      => VirtualFeeds.Saved,
            "downloaded" => VirtualFeeds.Downloaded,
            "history"    => VirtualFeeds.History,
            "queue"      => VirtualFeeds.Queue,
            _            => null
        };

        if (target is Guid fid)
        {
            data.LastSelectedFeedId = fid;
            _ = persist();
            ui.SelectFeed(fid);
            ui.SetEpisodesForFeed(fid, data.Episodes);
        }
        else ui.ShowOsd("usage: :feed all|saved|downloaded|history|queue");
    }

    public static void RemoveSelectedFeed(Shell ui, AppData data, Func<Task> persist)
    {
        var fid = ui.GetSelectedFeedId();
        if (fid is null) { ui.ShowOsd("No feed selected"); return; }

        if (fid == VirtualFeeds.All || fid == VirtualFeeds.Saved || fid == VirtualFeeds.Downloaded || fid == VirtualFeeds.History)
        { ui.ShowOsd("Can't remove virtual feeds"); return; }

        var feed = data.Feeds.FirstOrDefault(f => f.Id == fid);
        if (feed == null) { ui.ShowOsd("Feed not found"); return; }

        int removedEps = data.Episodes.RemoveAll(e => e.FeedId == fid);
        data.Feeds.RemoveAll(f => f.Id == fid);

        _ = persist();

        data.LastSelectedFeedId = VirtualFeeds.All;

        ui.SetFeeds(data.Feeds, data.LastSelectedFeedId);
        ViewModule.ApplyList(ui, data);

        ui.ShowOsd($"Removed feed: {feed.Title} ({removedEps} eps)");
    }
}

internal static class HistoryModule
{
    public static void ExecHistory(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg.StartsWith("clear"))
        {
            int count = 0;
            foreach (var e in data.Episodes)
            {
                if (e.Progress.LastPlayedAt != null) { e.Progress.LastPlayedAt = null; count++; }
            }
            _ = persist();
            ViewModule.ApplyList(ui, data);
            ui.ShowOsd($"History cleared ({count})");
            return;
        }

        if (arg.StartsWith("size"))
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var n) && n > 0)
            {
                data.HistorySize = Math.Clamp(n, 10, 10000);
                _ = persist();

                ui.SetHistoryLimit(data.HistorySize);
                ViewModule.ApplyList(ui, data);
                ui.ShowOsd($"History size = {data.HistorySize}");
                return;
            }
            ui.ShowOsd("usage: :history size <n>");
            return;
        }

        ui.ShowOsd("history: clear | size <n>");
    }
}

internal static class OpmlModule
{
    public static void ExecOpml(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var argv = args ?? Array.Empty<string>();
        if (argv.Length == 0) { ui.ShowOsd("usage: :opml import <path> [--update-titles] | :opml export [<path>]"); return; }

        var sub = argv[0].ToLowerInvariant();
        if (sub is "import")
        {
            if (argv.Length < 2) { ui.ShowOsd("usage: :opml import <path> [--update-titles]"); return; }

            var path = argv[1];
            bool updateTitles = argv.Any(a => string.Equals(a, "--update-titles", StringComparison.OrdinalIgnoreCase));

            string xml;
            try { xml = OpmlIo.ReadFile(path); }
            catch (Exception ex) { ui.ShowOsd($"import: read error ({ex.Message})", 2000); return; }

            OpmlDocument doc;
            try { doc = OpmlParser.Parse(xml); }
            catch (Exception ex) { ui.ShowOsd($"import: parse error ({ex.Message})", 2000); return; }

            var plan = OpmlImportPlanner.Plan(doc, data.Feeds, updateTitles);
            ui.ShowOsd($"OPML: new {plan.NewCount}, dup {plan.DuplicateCount}, invalid {plan.InvalidCount}", 1600);

            if (plan.NewCount == 0) return;

            int added = 0;
            foreach (var item in plan.NewItems())
            {
                var url = item.Entry.XmlUrl?.Trim();
                if (string.IsNullOrWhiteSpace(url)) continue;

                ui.RequestAddFeed(url);
                added++;
            }

            _ = persist();
            ui.RequestRefresh();
            ui.ShowOsd($"Imported {added} feed(s).", 1200);
            return;
        }

        if (sub is "export")
        {
            string? path = (argv.Length >= 2 ? argv[1] : null);
            if (string.IsNullOrWhiteSpace(path))
                path = OpmlIo.GetDefaultExportPath(baseName: "podliner-feeds.opml");

            string xml;
            try { xml = OpmlExporter.BuildXml(data.Feeds, "podliner feeds"); }
            catch (Exception ex) { ui.ShowOsd($"export: build error ({ex.Message})", 2000); return; }

            try
            {
                var used = OpmlIo.WriteFile(path!, xml, sanitizeFileNameIfNeeded: true, overwrite: true);
                ui.ShowOsd($"Exported → {used}", 1600);
            }
            catch (Exception ex) { ui.ShowOsd($"export: write error ({ex.Message})", 2000); }
            return;
        }

        ui.ShowOsd("usage: :opml import <path> [--update-titles] | :opml export [<path>]");
    }
}

internal static class IoModule
{
    public static void ExecOpen(string[] args, Shell ui, AppData data)
    {
        var mode = (args.Length > 0 ? args[0] : "site").Trim().ToLowerInvariant(); // "site" | "audio"
        var ep = ui.GetSelectedEpisode();
        if (ep == null) { ui.ShowOsd("no episode selected"); return; }

        string? url = null;

        if (mode == "audio") url = ep.AudioUrl;
        else
        {
            url = GetPropString(ep, "Link", "PageUrl", "Website", "WebsiteUrl", "HtmlUrl");
            if (string.IsNullOrWhiteSpace(url))
            {
                var feed = data.Feeds.FirstOrDefault(f => f.Id == ep.FeedId);
                url = GetPropString(feed, "Link", "Website", "WebsiteUrl", "HtmlUrl", "Home");
            }
            if (string.IsNullOrWhiteSpace(url)) url = ep.AudioUrl;
        }

        if (string.IsNullOrWhiteSpace(url)) { ui.ShowOsd("no URL to open"); return; }
        if (!TryOpenSystem(url)) ui.ShowOsd(url, 2000);
    }

    public static void ExecCopy(string[] args, Shell ui, AppData data)
    {
        var what = (args.Length > 0 ? args[0] : "url").Trim().ToLowerInvariant(); // url|title|guid
        var ep = ui.GetSelectedEpisode();
        if (ep == null) { ui.ShowOsd("no episode selected"); return; }

        string? text = what switch
        {
            "title" => ep.Title ?? "",
            "guid"  => GetPropString(ep, "Guid", "EpisodeGuid") ?? ep.Id.ToString(),
            _       => ep.AudioUrl ?? ""
        };

        if (string.IsNullOrWhiteSpace(text)) { ui.ShowOsd("nothing to copy"); return; }

        if (TryCopyToClipboard(text)) ui.ShowOsd("copied", 800);
        else ui.ShowOsd(text, 3000);
    }

    private static bool TryOpenSystem(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            { var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }; System.Diagnostics.Process.Start(psi); return true; }
            if (OperatingSystem.IsMacOS())
            { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", url) { UseShellExecute = false }); return true; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    private static string? GetPropString(object? obj, params string[] names)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        foreach (var n in names)
        {
            var p = t.GetProperty(n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (p != null && p.PropertyType == typeof(string))
            {
                var v = (string?)p.GetValue(obj);
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        }
        return null;
    }

    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -Command Set-Clipboard -Value @'\n{text}\n'@")
                { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(1200);
                return p != null && p.ExitCode == 0;
            }
            if (OperatingSystem.IsMacOS())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("pbcopy") { UseShellExecute = false, RedirectStandardInput = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p!.StandardInput.Write(text); p.StandardInput.Close(); p.WaitForExit(800);
                return true;
            }
            foreach (var tool in new[] { "xclip", "xsel" })
            {
                try
                {
                    var psi = tool == "xclip"
                        ? new System.Diagnostics.ProcessStartInfo("xclip", "-selection clipboard")
                        : new System.Diagnostics.ProcessStartInfo("xsel", "--clipboard --input");
                    psi.UseShellExecute = false; psi.RedirectStandardInput = true;
                    using var p = System.Diagnostics.Process.Start(psi);
                    p!.StandardInput.Write(text); p.StandardInput.Close(); p.WaitForExit(800);
                    return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }
}

internal static class QueueModule
{
    public static bool HandleQueue(string cmd, Shell ui, AppData data, Func<Task> saveAsync)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var t = cmd.Trim();

        if (!t.StartsWith(":queue", StringComparison.OrdinalIgnoreCase) &&
            !t.Equals("q", StringComparison.OrdinalIgnoreCase)) return false;

        if (t.Equals("q", StringComparison.OrdinalIgnoreCase)) t = ":queue add";

        string[] parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "add";

        void Refresh()
        {
            ui.SetQueueOrder(data.Queue);
            ui.RefreshEpisodesForSelectedFeed(data.Episodes);
        }
        async Task PersistLocal() { try { await saveAsync(); } catch { } }

        var ep = ui.GetSelectedEpisode();

        switch (sub)
        {
            case "add":
            case "toggle":
                if (ep == null) return true;
                if (data.Queue.Contains(ep.Id)) data.Queue.Remove(ep.Id);
                else data.Queue.Add(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "rm":
            case "remove":
                if (ep == null) return true;
                data.Queue.Remove(ep.Id);
                Refresh(); _ = PersistLocal(); return true;

            case "clear":
                data.Queue.Clear();
                Refresh(); _ = PersistLocal(); return true;

            case "shuffle":
            {
                var rnd = new Random();
                for (int i = data.Queue.Count - 1; i > 0; i--)
                { int j = rnd.Next(i + 1); (data.Queue[i], data.Queue[j]) = (data.Queue[j], data.Queue[i]); }
                Refresh(); _ = PersistLocal(); ui.ShowOsd("queue: shuffled", 900); return true;
            }
            case "uniq":
            {
                var seen = new HashSet<Guid>();
                var compact = new List<Guid>(data.Queue.Count);
                foreach (var id in data.Queue) if (seen.Add(id)) compact.Add(id);
                data.Queue.Clear(); data.Queue.AddRange(compact);
                Refresh(); _ = PersistLocal(); ui.ShowOsd("queue: uniq", 900); return true;
            }

            case "move":
            {
                var dir = (parts.Length >= 3 ? parts[2].ToLowerInvariant() : "down");
                var sel = ui.GetSelectedEpisode(); if (sel == null) return true;

                int idx = data.Queue.FindIndex(id => id == sel.Id);
                if (idx < 0) return true;

                int last = data.Queue.Count - 1;
                int target = idx;
                if (dir == "up") target = Math.Max(0, idx - 1);
                else if (dir == "down") target = Math.Min(last, idx + 1);
                else if (dir == "top") target = 0;
                else if (dir == "bottom") target = last;

                if (target != idx)
                {
                    var id = data.Queue[idx];
                    data.Queue.RemoveAt(idx);
                    data.Queue.Insert(target, id);
                    Refresh(); _ = PersistLocal();
                    ui.ShowOsd(target < idx ? "Moved ↑" : "Moved ↓");
                }
                return true;
            }

            default:
                return true;
        }
    }
}

internal static class DownloadsModule
{
    public static bool HandleDownloads(string cmd, Shell ui, AppData data, DownloadManager dlm, Func<Task> saveAsync)
    {
        cmd = (cmd ?? "").Trim();

        if (cmd.StartsWith(":downloads", StringComparison.OrdinalIgnoreCase))
        {
            var dparts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sub = (dparts.Length > 1 ? dparts[1].ToLowerInvariant() : "");

            if (string.IsNullOrEmpty(sub))
            {
                var q = data.DownloadQueue.Count;
                var running = data.DownloadMap.Count(kv => kv.Value.State == DownloadState.Running);
                var failed = data.DownloadMap.Count(kv => kv.Value.State == DownloadState.Failed);
                ui.ShowOsd($"downloads: queue {q}, running {running}, failed {failed}", 1500);
                return true;
            }

            if (sub == "retry-failed")
            {
                int n = 0;
                foreach (var (id, st) in data.DownloadMap.ToArray())
                {
                    if (st.State == DownloadState.Failed) { dlm.Enqueue(id); n++; }
                }
                _ = saveAsync();
                ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                ui.ShowOsd($"downloads: retried {n} failed", 1500);
                return true;
            }

            if (sub == "clear-queue")
            {
                int n = data.DownloadQueue.Count;
                data.DownloadQueue.Clear();
                _ = saveAsync();
                ui.RefreshEpisodesForSelectedFeed(data.Episodes);
                ui.ShowOsd($"downloads: cleared queue ({n})", 1200);
                return true;
            }

            if (sub == "open-dir")
            {
                var dir = GuessDownloadDir(data);
                if (!string.IsNullOrWhiteSpace(dir)) TryOpenSystem(dir);
                else ui.ShowOsd("downloads: no directory found", 1200);
                return true;
            }

            ui.ShowOsd("downloads: retry-failed | clear-queue | open-dir", 1200);
            return true;
        }

        if (!cmd.StartsWith(":dl", StringComparison.OrdinalIgnoreCase)
            && !cmd.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
            return false;

        var ep = ui.GetSelectedEpisode();
        if (ep == null) return true;

        var dlParts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var arg = (dlParts.Length > 1 ? dlParts[1].ToLowerInvariant() : "");

        switch (arg)
        {
            case "start":
                dlm.ForceFront(ep.Id);
                dlm.EnsureRunning();
                ui.ShowOsd("Downloading ⇣ (forced)");
                break;

            case "cancel":
                dlm.Cancel(ep.Id);
                data.DownloadMap.Remove(ep.Id);
                data.DownloadQueue.RemoveAll(x => x == ep.Id);
                ui.ShowOsd("Download canceled ✖");
                break;

            default:
            {
                var st = dlm.GetState(ep.Id);
                if (st == DownloadState.None || st == DownloadState.Canceled || st == DownloadState.Failed)
                {
                    dlm.Enqueue(ep.Id);
                    dlm.EnsureRunning();         // <— WICHTIG: starten!
                    ui.ShowOsd("Download queued ⌵");
                }
                else
                {
                    dlm.Cancel(ep.Id);
                    data.DownloadMap.Remove(ep.Id);
                    data.DownloadQueue.RemoveAll(x => x == ep.Id);
                    ui.ShowOsd("Download unqueued");
                }
                break;
            }

        }

        _ = saveAsync();
        ui.RefreshEpisodesForSelectedFeed(data.Episodes);
        return true;
    }

    public static void DlToggle(string arg, Shell ui, AppData data, Func<Task> persist, DownloadManager dlm)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool wantOn;
        if (arg is "on" or "true" or "+") wantOn = true;
        else if (arg is "off" or "false" or "-") wantOn = false;
        else wantOn = !Program.IsDownloaded(ep.Id);

        if (wantOn)
        {
            dlm.Enqueue(ep.Id);
            dlm.EnsureRunning();
            ui.ShowOsd("Download queued ⌵");
        }
        else
        {
            dlm.Cancel(ep.Id);
            data.DownloadQueue.RemoveAll(x => x == ep.Id);
            data.DownloadMap.Remove(ep.Id);
            ui.ShowOsd("Download canceled ✖");
        }

        _ = persist();
        ViewModule.ApplyList(ui, data);
    }

    private static string? GuessDownloadDir(AppData data)
    {
        try
        {
            var any = data.DownloadMap.Values
                .Where(v => v.State == DownloadState.Done && !string.IsNullOrWhiteSpace(v.LocalPath))
                .Select(v => System.IO.Path.GetDirectoryName(v.LocalPath!)!)
                .FirstOrDefault(p => p != null && System.IO.Directory.Exists(p));
            return any;
        }
        catch { return null; }
    }

    private static bool TryOpenSystem(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            { var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }; System.Diagnostics.Process.Start(psi); return true; }
            if (OperatingSystem.IsMacOS())
            { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", url) { UseShellExecute = false }); return true; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }
}

// ============================================================================
// Shared Utilities
// ============================================================================
internal static class VirtualFeeds
{
    public static readonly Guid All        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
    public static readonly Guid Saved      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
    public static readonly Guid Downloaded = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");
    public static readonly Guid History    = Guid.Parse("00000000-0000-0000-0000-00000000B157");
    public static readonly Guid Queue      = Guid.Parse("00000000-0000-0000-0000-00000000C0DE");
}

internal static class ListBuilder
{
    public static List<Episode> BuildCurrentList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        IEnumerable<Episode> baseList = data.Episodes;

        if (feedId == null) return new List<Episode>();
        if (data.UnplayedOnly) baseList = baseList.Where(e => !e.ManuallyMarkedPlayed);

        if (feedId == VirtualFeeds.Saved)           baseList = baseList.Where(e => e.Saved);
        else if (feedId == VirtualFeeds.Downloaded) baseList = baseList.Where(e => Program.IsDownloaded(e.Id));
        else if (feedId == VirtualFeeds.History)    baseList = baseList.Where(e => e.Progress.LastPlayedAt != null);
        else if (feedId != VirtualFeeds.All)        baseList = baseList.Where(e => e.FeedId == feedId);

        if (feedId == VirtualFeeds.History)
        {
            return baseList
                .OrderByDescending(e => e.Progress.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.Progress.LastPosMs)
                .ToList();
        }

        return baseList
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();
    }
}
