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
    public static void Handle(string raw,
                              IPlayer player,
                              PlaybackCoordinator playback,
                              Shell ui,
                              MemoryLogSink mem,
                              AppData data,
                              Func<Task> persist)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var cmd = raw.Trim();

        // --- quick commands via keys ---
        if (cmd.Equals(":toggle", StringComparison.OrdinalIgnoreCase))
        {
            player.TogglePause();
            ui.UpdatePlayerUI(player.State);
            return;
        }

        if (cmd.StartsWith(":vol",   StringComparison.OrdinalIgnoreCase)) { Volume(cmd[4..].Trim(), player, data, persist, ui); return; }
        if (cmd.StartsWith(":speed", StringComparison.OrdinalIgnoreCase)) { Speed (cmd[6..].Trim(), player, data, persist, ui); return; }

        if (cmd.StartsWith(":seek",  StringComparison.OrdinalIgnoreCase)) { Seek(cmd[5..].Trim(), player); return; }
        if (cmd.StartsWith(":logs",  StringComparison.OrdinalIgnoreCase)) { Logs(cmd[5..].Trim(), ui); return; }
        if (cmd is ":h" or ":help") { ui.ShowKeysHelp(); return; }
        if (cmd is ":q" or ":quit") { ui.RequestQuit(); return; }

        // --- SAVE / UNSAVE ------------------------------------------------------
        if (cmd.StartsWith(":save", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd.Length > 5 ? cmd[5..].Trim().ToLowerInvariant() : "";
            SaveToggle(arg, ui, data, persist);
            return;
        }
        
        // --- DOWNLOAD FLAG ----------------------------------------------------------
        if (cmd.StartsWith(":dl", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith(":download", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd.Contains(' ')
                ? cmd[(cmd.IndexOf(' ') + 1)..].Trim().ToLowerInvariant()
                : "";
            DlToggle(arg, ui, data, persist);
            ApplyList(ui, data); // damit Virtual-Feed „Downloaded“ sofort aktualisiert
            return;
        }


        // --- Navigation / Playback helpers -------------------------------------
        if (cmd.Equals(":next", StringComparison.OrdinalIgnoreCase))      { SelectRelative(+1, ui, data); return; }
        if (cmd.Equals(":prev", StringComparison.OrdinalIgnoreCase))      { SelectRelative(-1, ui, data); return; }

        if (cmd.Equals(":play-next", StringComparison.OrdinalIgnoreCase)) { SelectRelative(+1, ui, data, playAfterSelect: true, playback: playback); return; }
        if (cmd.Equals(":play-prev", StringComparison.OrdinalIgnoreCase)) { SelectRelative(-1, ui, data, playAfterSelect: true, playback: playback); return; }

        if (cmd.StartsWith(":replay", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd.Length > 7 ? cmd[7..].Trim() : "";
            Replay(arg, player, ui);
            return;
        }

        if (cmd.StartsWith(":goto", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd.Length > 5 ? cmd[5..].Trim().ToLowerInvariant() : "";
            if (arg is "top" or "start") { SelectAbsolute(0, ui, data); return; }
            if (arg is "bottom" or "end") { SelectAbsolute(int.MaxValue, ui, data); return; }
            return;
        }

        // Vim-ähnlich: zt/zz/zb + H/M/L -> wir mappen das pragmatisch auf Auswahl an top/middle/bottom
        if (cmd.Equals(":zt", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":H", StringComparison.OrdinalIgnoreCase))
        { SelectAbsolute(0, ui, data); return; }
        if (cmd.Equals(":zz", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":M", StringComparison.OrdinalIgnoreCase))
        { SelectMiddle(ui, data); return; }
        if (cmd.Equals(":zb", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":L", StringComparison.OrdinalIgnoreCase))
        { SelectAbsolute(int.MaxValue, ui, data); return; }
        
        if (cmd.StartsWith(":sort", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd.Length > 5 ? cmd[5..].Trim() : "";
            HandleSort(arg, ui, data, persist);
            // Liste neu anwenden -> Shell ruft SetEpisodesForFeed(...), dort greift der Sorter.
            ApplyList(ui, data);
            return;
        }

        
        if (cmd.Equals(":rm-feed", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals(":remove-feed", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals(":feed remove", StringComparison.OrdinalIgnoreCase))
        {
            RemoveSelectedFeed(ui, data, persist);
            return;
        }



        // --- view / player placement ---
        if (cmd.StartsWith(":player", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd[7..].Trim().ToLowerInvariant();
            if (arg is "toggle" or "" or null)
            {
                ui.TogglePlayerPlacement();
                data.PlayerAtTop = !data.PlayerAtTop;
                _ = persist();
                return;
            }
            if (arg is "top")
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
            return;
        }

        if (cmd.StartsWith(":filter", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd[7..].Trim().ToLowerInvariant();
            if (arg is "unplayed" or "only") data.UnplayedOnly = true;
            else if (arg is "all")           data.UnplayedOnly = false;
            else if (arg is "toggle" or "" or null) data.UnplayedOnly = !data.UnplayedOnly;

            ui.SetUnplayedFilterVisual(data.UnplayedOnly);
            _ = persist();
            ApplyList(ui, data);
            return;
        }

        if (cmd.Equals(":next-unplayed", StringComparison.OrdinalIgnoreCase)) { JumpUnplayed(+1, ui, playback, data); return; }
        if (cmd.Equals(":prev-unplayed", StringComparison.OrdinalIgnoreCase)) { JumpUnplayed(-1, ui, playback, data); return; }

        // --- add/refresh (nur Bequemlichkeit; Hauptfluss hängt an Program/Shell) ---
        if (cmd.StartsWith(":add ", StringComparison.OrdinalIgnoreCase)) { ui.RequestAddFeed(cmd[5..].Trim()); return; }

        if (cmd.StartsWith(":refresh", StringComparison.OrdinalIgnoreCase))
        {
            ui.ShowOsd("Refreshing…", 1500);   // kleines Overlay während der Aktualisierung
            ui.RequestRefresh();                // feuert den eigentlichen Refresh (async)
            return;
        }
        ui.ShowOsd("Refreshed ✓", 1200);
        
        // --- HISTORY --------------------------------------------------------------
        if (cmd.StartsWith(":history", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd.Length > 8 ? cmd[8..].Trim().ToLowerInvariant() : "";

            if (arg.StartsWith("clear"))
            {
                // Nur Verlauf leeren: LastPlayedAt=null, Played bleibt unberührt
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
                // :history size <n>
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var n) && n > 0)
                {
                    data.HistorySize = Math.Clamp(n, 10, 10000);
                    _ = persist();

                    // UI direkt aktualisieren (auch wenn der User gerade im History-Tab steht)
                    ui.SetHistoryLimit(data.HistorySize);
                    ApplyList(ui, data);
                    ui.ShowOsd($"History size = {data.HistorySize}");
                    return;
                }
                ui.ShowOsd("usage: :history size <n>");
                return;
            }

            ui.ShowOsd("history: clear | size <n>");
            return;
        }


        
        
        // unknown → ignore
    }

    // ---- helpers ------------------------------------------------------------

    public static void ApplyList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        if (feedId is null) return;

        // Basis: alle Episoden aus den Daten
        IEnumerable<Episode> list = data.Episodes;

        // Unplayed-Filter anwenden (Feed-Filter übernimmt Shell.SetEpisodesForFeed)
        if (data.UnplayedOnly) list = list.Where(e => !e.Played);

        if (feedId is Guid fid)
            ui.SetEpisodesForFeed(fid, list);
    }
    
    static void HandleSort(string arg, Shell ui, AppData data, Func<Task> persist)
    {
        // :sort show
        if (arg.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}");
            return;
        }

        // :sort reset
        if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            data.SortBy = "pubdate";
            data.SortDir = "desc";
            _ = persist();
            ui.ShowOsd("sort: pubdate desc");
            return;
        }

        // :sort reverse
        if (arg.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        {
            data.SortDir = (data.SortDir?.Equals("desc", StringComparison.OrdinalIgnoreCase) == true) ? "asc" : "desc";
            _ = persist();
            ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}");
            return;
        }

        // :sort by <key> [asc|desc]
        // akzeptierte keys
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

        // Fallback: kurze Hilfe
        ui.ShowOsd("sort: by pubdate|title|played|progress|feed [asc|desc]");
    }
    
    static void RemoveSelectedFeed(Shell ui, AppData data, Func<Task> persist)
    {
        var fid = ui.GetSelectedFeedId();
        if (fid is null) { ui.ShowOsd("No feed selected"); return; }

        // Virtuelle Feeds schützen
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

        if (feedId == FEED_ALL)
        {
            // alles
        }
        else if (feedId == FEED_SAVED)
        {
            baseList = baseList.Where(e => e.Saved);
        }
        else if (feedId == FEED_DOWNLOADED)
        {
            baseList = baseList.Where(e => e.Downloaded);
        }
        else if (feedId == FEED_HISTORY)
        {
            baseList = baseList.Where(e => e.LastPlayedAt != null);
            return baseList
                .OrderByDescending(e => e.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.LastPosMs ?? 0)
                .ToList();
        }
        else
        {
            baseList = baseList.Where(e => e.FeedId == feedId);
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

        // Index in der UI selektieren
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
    
    // CommandRouter.cs
// Neue öffentliche Hilfsmethode komplett einfügen (in die CommandRouter-Klasse)



    public static bool HandleQueue(string cmd, Shell ui, AppData data, Func<Task> saveAsync)
{
    if (string.IsNullOrWhiteSpace(cmd)) return false;
    var t = cmd.Trim();

    // Nur Kommandos, die mit ":queue" beginnen, werden hier behandelt
    if (!t.StartsWith(":queue", StringComparison.OrdinalIgnoreCase) &&
        !t.Equals("q", StringComparison.OrdinalIgnoreCase)) return false;

    // Kurzformen:
    if (t.Equals("q", StringComparison.OrdinalIgnoreCase)) t = ":queue add";

    string[] parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "add";

    // Helpers
    void Refresh()
    {
        ui.SetQueueOrder(data.Queue);
        ui.RefreshEpisodesForSelectedFeed(data.Episodes);
    }
    async Task Persist()
    {
        try { await saveAsync(); } catch { }
    }

    // Aktuell selektierte Episode
    var ep = ui.GetSelectedEpisode();

    switch (sub)
    {
        case "add":
        case "toggle":
            if (ep == null) return true;
            if (data.Queue.Contains(ep.Id)) data.Queue.Remove(ep.Id);
            else data.Queue.Add(ep.Id);
            Refresh();
            _ = Persist();
            return true;

        case "rm":
        case "remove":
            if (ep == null) return true;
            data.Queue.Remove(ep.Id);
            Refresh();
            _ = Persist();
            return true;

        case "clear":
            data.Queue.Clear();
            Refresh();
            _ = Persist();
            return true;

        case "move":
            {
                // :queue move <up|down|top|bottom>
                var dir = (parts.Length >= 3 ? parts[2].ToLowerInvariant() : "down");
                var sel = ui.GetSelectedEpisode();
                if (sel == null) return true;

                int idx = data.Queue.FindIndex(id => id == sel.Id);
                if (idx < 0) return true; // Selektion ist kein Queue-Item

                int last = data.Queue.Count - 1;
                int target = idx;
                if (dir == "up")      target = Math.Max(0, idx - 1);
                else if (dir == "down")   target = Math.Min(last, idx + 1);
                else if (dir == "top")    target = 0;
                else if (dir == "bottom") target = last;

                if (target != idx)
                {
                    var id = data.Queue[idx];
                    data.Queue.RemoveAt(idx);
                    data.Queue.Insert(target, id);
                    Refresh();
                    _ = Persist();
                    ui.ShowOsd(target < idx ? "Moved ↑" : "Moved ↓");
                }
                return true;
            }

        default:
            // Unbekannt → ignorieren, aber als "behandelt" zählen, damit es nicht woanders landet
            return true;
    }
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
            // komplett neu von vorn
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
            // fallback: wie ohne arg
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
            newVal = !ep.Saved; // toggle default

        ep.Saved = newVal;
        _ = persist();

        // UI aktualisieren (Liste neu aufbauen, damit "★ Saved" sofort greift)
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
            newVal = !ep.Downloaded; // toggle default

        ep.Downloaded = newVal;
        _ = persist();

        ApplyList(ui, data);
        ui.ShowOsd(newVal ? "Marked ⬇ Downloaded" : "Removed ⬇");
    }

}
