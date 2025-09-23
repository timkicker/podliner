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
        
        // --- sort ------------------------------------------------------
        if (cmd.StartsWith(":sort", StringComparison.OrdinalIgnoreCase))
        {
            var arg = cmd.Length > 5 ? cmd[5..].Trim() : "";
            HandleSort(arg, ui, data, persist);
            // Liste neu anwenden (Shell holt Sortierer aus Program)
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

        // Virtuelle Feeds schützen (IDs wie in Shell)
        var FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
        var FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
        var FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");
        if (fid == FEED_ALL || fid == FEED_SAVED || fid == FEED_DOWNLOADED)
        {
            ui.ShowOsd("Can't remove virtual feeds");
            return;
        }

        var feed = data.Feeds.FirstOrDefault(f => f.Id == fid);
        if (feed == null) { ui.ShowOsd("Feed not found"); return; }

        // Episoden dieses Feeds entfernen
        int removedEps = data.Episodes.RemoveAll(e => e.FeedId == fid);

        // Feed entfernen
        data.Feeds.RemoveAll(f => f.Id == fid);

        // persistieren
        _ = persist();

        // UI aktualisieren: Feeds neu setzen (Shell fügt virtuelle wieder oben ein)
        ui.SetFeeds(data.Feeds);

        // Episodenliste neu anwenden (für neue Feed-Auswahl)
        ApplyList(ui, data);

        ui.ShowOsd($"Removed feed: {feed.Title} ({removedEps} eps)");
    }



    static List<Episode> BuildCurrentList(Shell ui, AppData data)
    {
        var feedId = ui.GetSelectedFeedId();
        IEnumerable<Episode> baseList = data.Episodes;

        // Virtuelle Feeds berücksichtigen
        var FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
        var FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
        var FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");

        if (feedId == null) return new List<Episode>();

        if (data.UnplayedOnly) baseList = baseList.Where(e => !e.Played);

        if (feedId == FEED_ALL)
        {
            // alle Episoden
        }
        else if (feedId == FEED_SAVED)
        {
            baseList = baseList.Where(e => e.Saved);
        }
        else if (feedId == FEED_DOWNLOADED)
        {
            baseList = baseList.Where(e => e.Downloaded);
        }
        else
        {
            baseList = baseList.Where(e => e.FeedId == feedId);
        }

        // gleiche Sortierung wie in der UI (PubDate desc als Default)
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

        // Virtuelle Feeds berücksichtigen (IDs aus Shell bekannt halten)
        var FEED_ALL        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
        var FEED_SAVED      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
        var FEED_DOWNLOADED = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");

        if (feedId == FEED_SAVED)
            baseList = baseList.Where(e => e.Saved);
        else if (feedId == FEED_DOWNLOADED)
            baseList = baseList.Where(e => e.Downloaded);
        else if (feedId != FEED_ALL)
            baseList = baseList.Where(e => e.FeedId == feedId);

        var eps = baseList
            .OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue)
            .ToList();

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
