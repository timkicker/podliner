using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;

namespace StuiPodcast.App.Command.UseCases;

// Drives the episode and feed pane rendering: list refresh, search, sort,
// filter, player-bar placement, theme. All view-affecting commands flow
// through here so list-refresh semantics live in one place.
internal sealed class ViewUseCase
{
    readonly IUiShell _ui;
    readonly AppData _data;
    readonly Func<Task> _persist;
    readonly IEpisodeStore _episodes;
    readonly IFeedStore _feeds;

    public ViewUseCase(IUiShell ui, AppData data, Func<Task> persist, IEpisodeStore episodes, IFeedStore feeds)
    {
        _ui = ui;
        _data = data;
        _persist = persist;
        _episodes = episodes;
        _feeds = feeds;
    }

    public void ApplyList()
    {
        var feedId = _ui.GetSelectedFeedId();
        if (feedId is null) return;

        IEnumerable<Episode> list = _episodes.Snapshot();
        if (_data.UnplayedOnly) list = list.Where(e => !e.ManuallyMarkedPlayed);
        if (feedId is Guid fid) _ui.SetEpisodesForFeed(fid, list);
    }

    public void ApplyFeedList()
    {
        var sorted = UiComposer.ApplyFeedSort(_feeds.Snapshot(), _data, _episodes).ToList();
        _ui.SetFeeds(sorted, _data.LastSelectedFeedId);
    }

    public void ExecSearch(string[] args)
    {
        var query = string.Join(' ', args ?? Array.Empty<string>()).Trim();

        if (string.Equals(query, "clear", StringComparison.OrdinalIgnoreCase))
        {
            var fid = _ui.GetSelectedFeedId();
            if (fid != null) _ui.SetEpisodesForFeed(fid.Value, _episodes.Snapshot());
            _ui.ShowOsd("search cleared", 800);
            return;
        }

        var feedId = _ui.GetSelectedFeedId();
        IEnumerable<Episode> list = _episodes.Snapshot();

        if (feedId != null) list = list.Where(e => e.FeedId == feedId.Value);

        if (!string.IsNullOrWhiteSpace(query))
        {
            list = list.Where(e =>
                (e.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (feedId != null) _ui.SetEpisodesForFeed(feedId.Value, list);
        _ui.ShowOsd($"search: {query}", 900);
    }

    public void ExecSort(string[] args)
    {
        var parts = args ?? Array.Empty<string>();
        if (parts.Length > 0 && parts[0].Equals("feeds", StringComparison.OrdinalIgnoreCase))
        {
            var feedArg = string.Join(' ', parts.Skip(1)).Trim();
            HandleFeedSort(feedArg);
            return;
        }
        var arg = string.Join(' ', parts).Trim();
        HandleSort(arg);
    }

    public void ExecFilter(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();

        if (arg is "unplayed" or "only") _data.UnplayedOnly = true;
        else if (arg is "all") _data.UnplayedOnly = false;
        else if (arg is "" or "toggle") _data.UnplayedOnly = !_data.UnplayedOnly;
        else { _ui.ShowOsd("usage: :filter unplayed|all|toggle"); return; }

        _ui.SetUnplayedFilterVisual(_data.UnplayedOnly);
        _ = _persist();
    }

    public void ExecPlayerBar(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "toggle")
        { _ui.TogglePlayerPlacement(); _data.PlayerAtTop = !_data.PlayerAtTop; _ = _persist(); return; }
        if (arg == "top")
        { _ui.SetPlayerPlacement(true); _data.PlayerAtTop = true; _ = _persist(); return; }
        if (arg is "bottom" or "bot")
        { _ui.SetPlayerPlacement(false); _data.PlayerAtTop = false; _ = _persist(); return; }
    }

    public void ExecTheme(string[] args)
    {
        var arg = string.Join(' ', args ?? Array.Empty<string>()).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "toggle")
        {
            _ui.ToggleTheme();
            _data.ThemePref = null;
            _ = _persist();
            return;
        }

        ThemeMode mode = arg switch
        {
            "base"   => ThemeMode.Base,
            "accent" => ThemeMode.MenuAccent,
            "native" => ThemeMode.Native,
            "auto"   => OperatingSystem.IsWindows() ? ThemeMode.Base : ThemeMode.MenuAccent,
            _        => OperatingSystem.IsWindows() ? ThemeMode.Base : ThemeMode.MenuAccent
        };

        try { _ui.SetTheme(mode); _data.ThemePref = mode.ToString(); _ = _persist(); _ui.ShowOsd($"theme: {mode}"); }
        catch { _ui.ShowOsd("theme: failed"); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    void HandleFeedSort(string arg)
    {
        if (arg.Equals("show", StringComparison.OrdinalIgnoreCase))
        { _ui.ShowOsd($"sort feeds: {_data.FeedSortBy} {_data.FeedSortDir}"); return; }

        if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _data.FeedSortBy = "title"; _data.FeedSortDir = "asc";
            _ = _persist(); ApplyFeedList();
            _ui.ShowOsd("sort feeds: title asc"); return;
        }

        string[] keys = new[] { "title", "updated", "unplayed" };
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 1 && parts[0].Equals("by", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                var key = parts[1].ToLowerInvariant();
                if (!keys.Contains(key)) { _ui.ShowOsd("sort feeds: invalid key"); return; }
                _data.FeedSortBy = key;
                if (parts.Length >= 3)
                { var dir = parts[2].ToLowerInvariant(); if (dir is "asc" or "desc") _data.FeedSortDir = dir; }
                _ = _persist(); ApplyFeedList();
                _ui.ShowOsd($"sort feeds: {_data.FeedSortBy} {_data.FeedSortDir}"); return;
            }
        }

        _ui.ShowOsd("sort feeds: by title|updated|unplayed [asc|desc]");
    }

    void HandleSort(string arg)
    {
        if (arg.Equals("show", StringComparison.OrdinalIgnoreCase)) { _ui.ShowOsd($"sort: {_data.SortBy} {_data.SortDir}"); return; }
        if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        { _data.SortBy = "pubdate"; _data.SortDir = "desc"; _ = _persist(); _ui.ShowOsd("sort: pubdate desc"); return; }

        if (arg.Equals("reverse", StringComparison.OrdinalIgnoreCase))
        { _data.SortDir = _data.SortDir?.Equals("desc", StringComparison.OrdinalIgnoreCase) == true ? "asc" : "desc"; _ = _persist(); _ui.ShowOsd($"sort: {_data.SortBy} {_data.SortDir}"); return; }

        string[] keys = new[] { "pubdate", "title", "played", "progress", "feed" };
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 1 && parts[0].Equals("by", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                var key = parts[1].ToLowerInvariant();
                if (!keys.Contains(key)) { _ui.ShowOsd("sort: invalid key"); return; }
                _data.SortBy = key;

                if (parts.Length >= 3)
                {
                    var dir = parts[2].ToLowerInvariant();
                    if (dir is "asc" or "desc") _data.SortDir = dir;
                }

                _ = _persist();
                _ui.ShowOsd($"sort: {_data.SortBy} {_data.SortDir}");
                return;
            }
        }

        _ui.ShowOsd("sort: by pubdate|title|played|progress|feed [asc|desc]");
    }
}
