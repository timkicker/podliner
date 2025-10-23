using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.Module;

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

        Shell.ThemeMode mode = arg switch
        {
            "base"   => Shell.ThemeMode.Base,
            "accent" => Shell.ThemeMode.MenuAccent,
            "native" => Shell.ThemeMode.Native,
            "auto"   => OperatingSystem.IsWindows() ? Shell.ThemeMode.Base : Shell.ThemeMode.MenuAccent,
            _        => OperatingSystem.IsWindows() ? Shell.ThemeMode.Base : Shell.ThemeMode.MenuAccent
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
        { data.SortDir = data.SortDir?.Equals("desc", StringComparison.OrdinalIgnoreCase) == true ? "asc" : "desc"; _ = persist(); ui.ShowOsd($"sort: {data.SortBy} {data.SortDir}"); return; }

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
