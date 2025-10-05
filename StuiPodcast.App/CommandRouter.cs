using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra;

static class CommandRouter
{
    // --------------------------- Public API ---------------------------

    public static void Handle(string raw,
                              IPlayer player,
                              PlaybackCoordinator playback,
                              Shell ui,
                              MemoryLogSink mem,
                              AppData data,
                              Func<Task> persist,
                              DownloadManager dlm) // <— RE-ADDED
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        var tokens = Tokenize(raw.Trim());
        if (tokens.Length == 0) return;

        // Pre-dispatch fastpaths
        if (HandleQueue(raw, ui, data, persist)) return;
        if (HandleDownloads(raw, ui, data, dlm, persist)) return; // <— USE dlm

        if (raw.StartsWith(":dl", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback: manueller Toggle, falls Kommando ohne sub-arg kommt
            var arg = raw.Contains(' ')
                ? raw[(raw.IndexOf(' ') + 1)..].Trim().ToLowerInvariant()
                : "";
            DlToggle(arg, ui, data, persist);
            ApplyList(ui, data);
            return;
        }

        var (cmdText, args) = SplitCmd(tokens);
        var top = MapTop(cmdText);

        switch (top)
        {
            // Basics
            case TopCommand.Help:         ui.ShowKeysHelp(); return;
            case TopCommand.Quit:         ui.RequestQuit();  return;
            case TopCommand.Logs:         ExecLogs(args, ui); return;

            // Playback & Player
            case TopCommand.Toggle:       player.TogglePause(); ui.UpdatePlayerUI(player.State); return;
            case TopCommand.Seek:         ExecSeek(args, player); return;
            case TopCommand.Volume:       ExecVolume(args, player, data, persist, ui); return;
            case TopCommand.Speed:        ExecSpeed(args, player, data, persist, ui);  return;
            case TopCommand.Replay:       ExecReplay(args, player, ui); return;
            case TopCommand.PlayNext:     SelectRelative(+1, ui, data, playAfterSelect: true, playback: playback); return;
            case TopCommand.PlayPrev:     SelectRelative(-1, ui, data, playAfterSelect: true, playback: playback); return;
            case TopCommand.Next:         SelectRelative(+1, ui, data); return;
            case TopCommand.Prev:         SelectRelative(-1, ui, data); return;

            // Navigation helpers
            case TopCommand.Goto:         ExecGoto(args, ui, data); return;
            case TopCommand.VimTop:       SelectAbsolute(0, ui, data); return;
            case TopCommand.VimMiddle:    SelectMiddle(ui, data);     return;
            case TopCommand.VimBottom:    SelectAbsolute(int.MaxValue, ui, data); return;
            case TopCommand.NextUnplayed: JumpUnplayed(+1, ui, playback, data); return;
            case TopCommand.PrevUnplayed: JumpUnplayed(-1, ui, playback, data); return;

            // Data flags (save/download)
            case TopCommand.Save:         ExecSave(args, ui, data, persist); return;

            // Sorting / Filtering / Player-Placement
            case TopCommand.Sort:         ExecSort(args, ui, data, persist); ApplyList(ui, data); return;
            case TopCommand.Filter:       ExecFilter(args, ui, data, persist); ApplyList(ui, data); return;
            case TopCommand.PlayerBar:    ExecPlayerBar(args, ui, data, persist); return;

            // Network & play-source
            case TopCommand.Net:          ExecNet(args, ui, data, persist); return;
            case TopCommand.PlaySource:   ExecPlaySource(args, ui, data, persist); return;

            // Feeds / Refresh
            case TopCommand.AddFeed:      ExecAddFeed(args, ui); return;
            case TopCommand.Refresh:      ui.ShowOsd("Refreshing…", 600); ui.RequestRefresh(); return;
            case TopCommand.RemoveFeed:   RemoveSelectedFeed(ui, data, persist); return;

            // History
            case TopCommand.History:      ExecHistory(args, ui, data, persist); return;

            // Unknown or passthrough:
            default: return;
        }
    }

    // --------------------------- Tokenize / Parse ---------------------------

    private static string[] Tokenize(string raw)
    {
        var list = new List<string>();
        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();
        foreach (var ch in raw)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(ch);
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

    private enum TopCommand
    {
        Unknown,
        Help, Quit, Logs,
        Toggle, Seek, Volume, Speed, Replay,
        Next, Prev, PlayNext, PlayPrev,
        Goto, VimTop, VimMiddle, VimBottom,
        NextUnplayed, PrevUnplayed,
        Save, Sort, Filter, PlayerBar,
        Net, PlaySource,
        AddFeed, Refresh, RemoveFeed,
        History
    }

    private static TopCommand MapTop(string cmd)
    {
        cmd = cmd?.Trim() ?? "";
        if (cmd.Equals(":h", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":help", StringComparison.OrdinalIgnoreCase)) return TopCommand.Help;
        if (cmd.Equals(":q", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":quit", StringComparison.OrdinalIgnoreCase)) return TopCommand.Quit;
        if (cmd.StartsWith(":logs", StringComparison.OrdinalIgnoreCase)) return TopCommand.Logs;

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
        if (cmd.Equals(":rm-feed", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals(":remove-feed", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals(":feed", StringComparison.OrdinalIgnoreCase)) return TopCommand.RemoveFeed;

        if (cmd.StartsWith(":history", StringComparison.OrdinalIgnoreCase)) return TopCommand.History;

        return TopCommand.Unknown;
    }

    // --------------------------- Command Executors ---------------------------

    private static void ExecLogs(string[] args, Shell ui)
    {
        var a = args.Length > 0 ? args[0] : "";
        int tail = 500;
        if (int.TryParse(a, out var n) && n > 0) tail = Math.Min(n, 5000);
        ui.ShowLogsOverlay(tail);
    }

    private static void ExecSeek(string[] args, IPlayer player)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Seek(arg, player);
    }

    private static void ExecVolume(string[] args, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Volume(arg, player, data, persist, ui);
    }

    private static void ExecSpeed(string[] args, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Speed(arg, player, data, persist, ui);
    }

    private static void ExecReplay(string[] args, IPlayer player, Shell ui)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        Replay(arg, player, ui);
    }

    private static void ExecGoto(string[] args, Shell ui, AppData data)
    {
        var arg = (args.Length > 0 ? args[0] : "").ToLowerInvariant();
        if (arg is "top" or "start") { SelectAbsolute(0, ui, data); return; }
        if (arg is "bottom" or "end") { SelectAbsolute(int.MaxValue, ui, data); return; }
    }

    private static void ExecSave(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        SaveToggle(arg, ui, data, persist);
    }

    private static void ExecSort(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        HandleSort(arg, ui, data, persist);
    }

    private static void ExecFilter(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg is "unplayed" or "only") data.UnplayedOnly = true;
        else if (arg is "all")          data.UnplayedOnly = false;
        else if (arg is "" or "toggle") data.UnplayedOnly = !data.UnplayedOnly;
        else { ui.ShowOsd("usage: :filter unplayed|all|toggle"); return; }

        ui.SetUnplayedFilterVisual(data.UnplayedOnly);
        _ = persist();
    }

    private static void ExecPlayerBar(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "toggle")
        {
            ui.TogglePlayerPlacement();
            data.PlayerAtTop = !data.PlayerAtTop;
            _ = persist();
            return;
        }
        if (arg == "top")
        {
            ui.SetPlayerPlacement(true);
            data.PlayerAtTop = true;
            _ = persist();
            return;
        }
        if (arg is "bottom" or "bot")
        {
            ui.SetPlayerPlacement(false);
            data.PlayerAtTop = false;
            _ = persist();
            return;
        }
    }

    private static void ExecNet(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "online" or "on")
        {
            data.NetworkOnline = true;
            _ = persist();
            ui.ShowOsd("Online", 600);
        }
        else if (arg is "offline" or "off")
        {
            data.NetworkOnline = false;
            _ = persist();
            ui.ShowOsd("Offline", 600);
        }
        else if (string.IsNullOrEmpty(arg) || arg == "toggle")
        {
            data.NetworkOnline = !data.NetworkOnline;
            _ = persist();
            ui.ShowOsd(data.NetworkOnline ? "Online" : "Offline", 600);
        }
        else
        {
            ui.ShowOsd("usage: :net online|offline|toggle", 1200);
        }

        ApplyList(ui, data);
        ui.RefreshEpisodesForSelectedFeed(data.Episodes);

        var nowId = ui.GetNowPlayingId();
        if (nowId != null)
        {
            var playing = data.Episodes.FirstOrDefault(x => x.Id == nowId);
            if (playing != null)
                ui.SetWindowTitle((!data.NetworkOnline ? "[OFFLINE] " : "") + (playing.Title ?? "—"));
        }
    }

    private static void ExecPlaySource(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (arg is "auto" or "local" or "remote")
        {
            data.PlaySource = arg;
            _ = persist();
            ui.ShowOsd($"play-source: {arg}");
        }
        else
        {
            ui.ShowOsd("usage: :play-source auto|local|remote");
        }
    }

    private static void ExecAddFeed(string[] args, Shell ui)
    {
        var url = string.Join(' ', args ?? Array.Empty<string>()).Trim();
        if (!string.IsNullOrEmpty(url))
            ui.RequestAddFeed(url);
        else
            ui.ShowOsd("usage: :add <rss-url>");
    }

    private static void ExecHistory(string[] args, Shell ui, AppData data, Func<Task> persist)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg.StartsWith("clear"))
        {
            int count = 0;
            foreach (var e in data.Episodes)
            {
                if (e.LastPlayedAt != null) { e.LastPlayedAt = null; count++; }
            }
            _ = persist();
            ApplyList(ui, data);
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
                ApplyList(ui, data);
                ui.ShowOsd($"History size = {data.HistorySize}");
                return;
            }
            ui.ShowOsd("usage: :history size <n>");
            return;
        }

        ui.ShowOsd("history: clear | size <n>");
    }

    // --------------------------- Existing Helpers (kept) ---------------------------

    public static void ApplyList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        IEnumerable<Episode> list = data.Episodes;
        if (data.UnplayedOnly) list = list.Where(e => !e.Played);

        if (feedId is Guid fid)
            ui.SetEpisodesForFeed(fid, list);
    }

    static void HandleSort(string arg, Shell ui, AppData data, Func<Task> persist)
    {
        if (arg.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}");
            return;
        }

        if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            data.SortBy = "pubdate";
            data.SortDir = "desc";
            _ = persist();
            ui.ShowOsd("sort: pubdate desc");
            return;
        }

        if (arg.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        {
            data.SortDir = (data.SortDir?.Equals("desc", StringComparison.OrdinalIgnoreCase) == true) ? "asc" : "desc";
            _ = persist();
            ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}");
            return;
        }

        string[] keys = new[] { "pubdate", "title", "played", "progress", "feed" };
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 1 && parts[0].Equals("by", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                var key = parts[1].ToLowerInvariant();
                if (!keys.Contains(key))
                {
                    ui.ShowOsd("sort: invalid key");
                    return;
                }
                data.SortBy = key;

                if (parts.Length >= 3)
                {
                    var dir = parts[2].ToLowerInvariant();
                    if (dir is "asc" or "desc")
                        data.SortDir = dir;
                }

                _ = persist();
                ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}");
                return;
            }
        }

        ui.ShowOsd("sort: by pubdate|title|played|progress|feed [asc|desc]");
    }

    static void RemoveSelectedFeed(Shell ui, AppData data, Func<Task> persist)
    {
        var fid = ui.GetSelectedFeedId();
        if (fid is null) { ui.ShowOsd("No feed selected"); return; }

        var FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
        var FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
        var FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");
        var FEED_HISTORY    = Guid.Parse("00000000-0000-0000-0000-00000000B157");

        if (fid == FEED_ALL || fid == FEED_SAVED || fid == FEED_DOWNLOADED || fid == FEED_HISTORY)
        {
            ui.ShowOsd("Can't remove virtual feeds");
            return;
        }

        var feed = data.Feeds.FirstOrDefault(f => f.Id == fid);
        if (feed == null) { ui.ShowOsd("Feed not found"); return; }

        int removedEps = data.Episodes.RemoveAll(e => e.FeedId == fid);
        data.Feeds.RemoveAll(f => f.Id == fid);

        _ = persist();

        ui.SetFeeds(data.Feeds);
        ApplyList(ui, data);

        ui.ShowOsd($"Removed feed: {feed.Title} ({removedEps} eps)");
    }

    static List<Episode> BuildCurrentList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        IEnumerable<Episode> baseList = data.Episodes;

        var FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
        var FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
        var FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");
        var FEED_HISTORY    = Guid.Parse("00000000-0000-0000-0000-00000000B157");

        if (feedId == null) return new List<Episode>();
        if (data.UnplayedOnly) baseList = baseList.Where(e => !e.Played);

        if (feedId == FEED_SAVED)           baseList = baseList.Where(e => e.Saved);
        else if (feedId == FEED_DOWNLOADED) baseList = baseList.Where(e => e.Downloaded);
        else if (feedId == FEED_HISTORY)    baseList = baseList.Where(e => e.LastPlayedAt != null);
        else if (feedId != FEED_ALL)        baseList = baseList.Where(e => e.FeedId == feedId);

        if (feedId == FEED_HISTORY)
        {
            return baseList
                .OrderByDescending(e => e.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.LastPosMs ?? 0)
                .ToList();
        }

        return baseList
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();
    }

    static void SelectRelative(int dir, Shell ui, AppData data, bool playAfterSelect = false, PlaybackCoordinator? playback = null)
    {
        var list = BuildCurrentList(ui, data);
        if (list.Count == 0) return;

        var cur = ui.GetSelectedEpisode();
        int idx = 0;
        if (cur != null)
        {
            var i = list.FindIndex(x => x.Id == cur.Id);
            idx = i >= 0 ? i : 0;
        }

        int target = dir > 0 ? Math.Min(idx + 1, list.Count - 1)
                             : Math.Max(idx - 1, 0);

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
        async Task PersistLocal()
        {
            try { await saveAsync(); } catch { }
        }

        var ep = ui.GetSelectedEpisode();

        switch (sub)
        {
            case "add":
            case "toggle":
                if (ep == null) return true;
                if (data.Queue.Contains(ep.Id)) data.Queue.Remove(ep.Id);
                else data.Queue.Add(ep.Id);
                Refresh();
                _ = PersistLocal();
                return true;

            case "rm":
            case "remove":
                if (ep == null) return true;
                data.Queue.Remove(ep.Id);
                Refresh();
                _ = PersistLocal();
                return true;

            case "clear":
                data.Queue.Clear();
                Refresh();
                _ = PersistLocal();
                return true;

            case "move":
            {
                var dir = (parts.Length >= 3 ? parts[2].ToLowerInvariant() : "down");
                var sel = ui.GetSelectedEpisode();
                if (sel == null) return true;

                int idx = data.Queue.FindIndex(id => id == sel.Id);
                if (idx < 0) return true;

                int last = data.Queue.Count - 1;
                int target = idx;
                if (dir == "up")         target = Math.Max(0, idx - 1);
                else if (dir == "down")  target = Math.Min(last, idx + 1);
                else if (dir == "top")   target = 0;
                else if (dir == "bottom")target = last;

                if (target != idx)
                {
                    var id = data.Queue[idx];
                    data.Queue.RemoveAt(idx);
                    data.Queue.Insert(target, id);
                    Refresh();
                    _ = PersistLocal();
                    ui.ShowOsd(target < idx ? "Moved ↑" : "Moved ↓");
                }
                return true;
            }

            default:
                return true;
        }
    }

    public static bool HandleDownloads(
        string cmd,
        Shell ui,
        AppData data,
        DownloadManager dlm,
        Func<Task> saveAsync)
    {
        cmd = (cmd ?? "").Trim();
        if (!cmd.StartsWith(":dl", StringComparison.OrdinalIgnoreCase)
            && !cmd.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
            return false;

        var ep = ui.GetSelectedEpisode();
        if (ep == null) return true;

        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var arg = (parts.Length > 1 ? parts[1].ToLowerInvariant() : "");

        switch (arg)
        {
            case "start":
                dlm.ForceFront(ep.Id);
                dlm.EnsureRunning();
                ui.ShowOsd("Forced ⇣");
                break;

            case "cancel":
                dlm.Cancel(ep.Id);
                data.DownloadMap.Remove(ep.Id);
                data.DownloadQueue.RemoveAll(x => x == ep.Id);
                ui.ShowOsd("Canceled");
                break;

            default:
                var st = dlm.GetState(ep.Id);
                if (st == DownloadState.None || st == DownloadState.Canceled || st == DownloadState.Failed)
                {
                    dlm.Enqueue(ep.Id);
                    ui.ShowOsd("Queued ⌵");
                }
                else
                {
                    dlm.Cancel(ep.Id);
                    data.DownloadMap.Remove(ep.Id);
                    data.DownloadQueue.RemoveAll(x => x == ep.Id);
                    ui.ShowOsd("Unqueued");
                }
                break;
        }

        _ = saveAsync();
        ui.RefreshEpisodesForSelectedFeed(data.Episodes);
        return true;
    }

    static void SelectAbsolute(int index, Shell ui, AppData data)
    {
        var list = BuildCurrentList(ui, data);
        if (list.Count == 0) return;

        int target = Math.Clamp(index, 0, list.Count - 1);
        ui.SelectEpisodeIndex(target);
    }

    static void SelectMiddle(Shell ui, AppData data)
    {
        var list = BuildCurrentList(ui, data);
        if (list.Count == 0) return;

        int target = list.Count / 2;
        ui.SelectEpisodeIndex(target);
    }

    static void Replay(string arg, IPlayer player, Shell ui)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            player.SeekTo(TimeSpan.Zero);
            ui.UpdatePlayerUI(player.State);
            return;
        }

        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec > 0)
        {
            player.SeekRelative(TimeSpan.FromSeconds(-sec));
            ui.UpdatePlayerUI(player.State);
        }
        else
        {
            player.SeekTo(TimeSpan.Zero);
            ui.UpdatePlayerUI(player.State);
        }
    }

    static void JumpUnplayed(int dir, Shell ui, PlaybackCoordinator playback, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        IEnumerable<Episode> baseList = data.Episodes;

        var FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
        var FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
        var FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");
        var FEED_HISTORY    = Guid.Parse("00000000-0000-0000-0000-00000000B157");

        if (feedId == FEED_SAVED)
            baseList = baseList.Where(e => e.Saved);
        else if (feedId == FEED_DOWNLOADED)
            baseList = baseList.Where(e => e.Downloaded);
        else if (feedId == FEED_HISTORY)
            baseList = baseList.Where(e => e.LastPlayedAt != null);
        else if (feedId != FEED_ALL)
            baseList = baseList.Where(e => e.FeedId == feedId);

        List<Episode> eps;
        if (feedId == FEED_HISTORY)
        {
            eps = baseList
                .OrderByDescending(e => e.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.LastPosMs ?? 0)
                .ToList();
        }
        else
        {
            eps = baseList
                .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
                .ToList();
        }

        if (eps.Count == 0) return;

        var cur = ui.GetSelectedEpisode();
        var startIdx = cur is null ? -1 : eps.FindIndex(x => ReferenceEquals(x, cur) || x.Id == cur.Id);
        int i = startIdx;

        for (int step = 0; step < eps.Count; step++)
        {
            i = dir > 0
                ? (i + 1 + eps.Count) % eps.Count
                : (i - 1 + eps.Count) % eps.Count;

            if (!eps[i].Played)
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

    static void SaveToggle(string arg, Shell ui, AppData data, Func<Task> persist)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool newVal = ep.Saved;

        if (arg is "on" or "true" or "+")
            newVal = true;
        else if (arg is "off" or "false" or "-")
            newVal = false;
        else
            newVal = !ep.Saved;

        ep.Saved = newVal;
        _ = persist();

        ApplyList(ui, data);
        ui.ShowOsd(newVal ? "Saved ★" : "Unsaved");
    }

    static void Seek(string arg, IPlayer player)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;

        var s = player.State;
        var len = s.Length ?? TimeSpan.Zero;

        // percentage
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

        // relative seconds +/-
        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relSecs))
        {
            player.SeekRelative(TimeSpan.FromSeconds(relSecs));
            return;
        }

        // mm:ss
        var parts = arg.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var mm) &&
            int.TryParse(parts[1], out var ss))
        {
            player.SeekTo(TimeSpan.FromSeconds(mm * 60 + ss));
            return;
        }

        // absolute seconds
        if (int.TryParse(arg, out var absSecs))
        {
            player.SeekTo(TimeSpan.FromSeconds(absSecs));
        }
    }

    static void Volume(string arg, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Volume0_100;

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            var v = Math.Clamp(cur + delta, 0, 100);
            player.SetVolume(v);
            data.Volume0_100 = v;
            _ = persist();
            ui.ShowOsd($"Vol {v}%");
            return;
        }
        if (int.TryParse(arg, out var abs))
        {
            var v = Math.Clamp(abs, 0, 100);
            player.SetVolume(v);
            data.Volume0_100 = v;
            _ = persist();
            ui.ShowOsd($"Vol {v}%");
        }
    }

    static void Speed(string arg, IPlayer player, AppData data, Func<Task> persist, Shell ui)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;
        var cur = player.State.Speed;

        if ((arg.StartsWith("+") || arg.StartsWith("-")) &&
            double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
        {
            var s = Math.Clamp(cur + delta, 0.25, 3.0);
            player.SetSpeed(s);
            data.Speed = s;
            _ = persist();
            ui.ShowOsd($"Speed {s:0.0}×");
            return;
        }
        if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var abs))
        {
            var s = Math.Clamp(abs, 0.25, 3.0);
            player.SetSpeed(s);
            data.Speed = s;
            _ = persist();
            ui.ShowOsd($"Speed {s:0.0}×");
        }
    }

    static void Logs(string arg, Shell ui)
    {
        int tail = 500;
        if (int.TryParse(arg, out var n) && n > 0) tail = Math.Min(n, 5000);
        ui.ShowLogsOverlay(tail);
    }

    static void DlToggle(string arg, Shell ui, AppData data, Func<Task> persist)
    {
        var ep = ui.GetSelectedEpisode();
        if (ep is null) return;

        bool newVal = ep.Downloaded;

        if (arg is "on" or "true" or "+")
            newVal = true;
        else if (arg is "off" or "false" or "-")
            newVal = false;
        else
            newVal = !ep.Downloaded;

        ep.Downloaded = newVal;
        _ = persist();

        ApplyList(ui, data);
        ui.ShowOsd(newVal ? "Marked ⬇ Downloaded" : "Removed ⬇");
    }
}
