using StuiPodcast.App.Services;
using StuiPodcast.App.UI.Controls;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

// Key-dispatch table for the three-pane TUI. UiShell wires the top-level
// views through Wire(), which attaches a single KeyPress handler per view
// that forwards to Handle(). All shell-level side-effects flow through the
// Bindings record so this class has no references back into UiShell's
// private state.
//
// The key map intentionally mirrors vim defaults: h/j/k/l for movement,
// space/enter for toggle/play, :/slash for command/search prompts, g/G for
// top/bottom, [ ] for speed adjust, n to repeat the last search.
internal static class UiShellKeyBindings
{
    public sealed record Bindings(
        UiEpisodesPane? EpisodesPane,
        UiPlayerPanel? Player,

        Func<Guid?> GetSelectedFeedId,
        Func<bool> IsFeedsPaneActive,
        Action<int> MoveList,
        Action FocusFeeds,
        Action FocusEpisodes,
        Action<int> JumpToUnplayed,

        Action<string> InvokeCommand,
        Action TogglePlayed,
        Action<int> ShowLogs,
        Action Quit,
        Action ToggleTheme,
        Action<string> ShowCommandBox,
        Action<string> ShowSearchBox,
        Action PlaySelected,

        Func<string?> GetLastSearch,
        Action<string> ApplySearch,
        Action NotifySelectedFeedChanged);

    public static void Wire(Bindings b, params View?[] views)
    {
        foreach (var v in views)
        {
            if (v is null) continue;
            v.KeyPress += e => { if (Handle(e, b)) e.Handled = true; };
        }
    }

    static bool Handle(View.KeyEventEventArgs e, Bindings b)
    {
        var key = e.KeyEvent.Key;
        var kv  = e.KeyEvent.KeyValue;

        // Swallow the raw Ctrl+C/V/X in the list views so they don't get
        // interpreted as text-input actions on a read-only list.
        if ((key & Key.CtrlMask) != 0)
        {
            var baseKey = key & ~Key.CtrlMask;
            if (baseKey == Key.C || baseKey == Key.V || baseKey == Key.X) { e.Handled = true; return true; }
        }

        bool inQueue = b.GetSelectedFeedId() is Guid fid && fid == VirtualFeedsCatalog.Queue;

        // Shift+J/K reorder the queue when that virtual feed is active,
        // otherwise they jump to the next/previous unplayed episode.
        if (kv == 'J') { if (inQueue) b.InvokeCommand(":queue move down"); else b.JumpToUnplayed(+1); return true; }
        if (kv == 'K') { if (inQueue) b.InvokeCommand(":queue move up");   else b.JumpToUnplayed(-1); return true; }

        if (kv == 'm' || kv == 'M') { b.TogglePlayed(); return true; }
        if (key == Key.F12) { b.ShowLogs(500); return true; }
        if (key == (Key.Q | Key.CtrlMask) || key == Key.Q || kv == 'Q' || kv == 'q') { b.Quit(); return true; }
        if (kv == 't' || kv == 'T') { b.ToggleTheme(); return true; }
        if (kv == 'u' || kv == 'U') { b.InvokeCommand(":filter toggle"); return true; }

        if (key == (Key)(':')) { b.ShowCommandBox(":"); return true; }
        if (key == (Key)('/')) { b.ShowSearchBox("/"); return true; }

        if (key == (Key)('h'))
        {
            if (IsDetailsTabActive(b.EpisodesPane)) SwitchToListTab(b.EpisodesPane);
            else b.FocusFeeds();
            return true;
        }

        if (key == (Key)('l'))
        {
            if (b.IsFeedsPaneActive()) b.FocusEpisodes();
            else if (!IsDetailsTabActive(b.EpisodesPane)) SwitchToDetailsTab(b.EpisodesPane);
            return true;
        }

        if (key == (Key)('j') || key == Key.CursorDown) { b.MoveList(+1); return true; }
        if (key == (Key)('k') || key == Key.CursorUp)   { b.MoveList(-1); return true; }

        if (kv == 'i' || kv == 'I') { SwitchToDetailsTab(b.EpisodesPane); return true; }

        if (key == Key.Esc && IsNonEpisodeTabActive(b.EpisodesPane))
        {
            SwitchToListTab(b.EpisodesPane);
            return true;
        }

        if (key == Key.Space)
        {
            b.Player?.OptimisticToggle();
            b.InvokeCommand(":toggle");
            return true;
        }

        if (key == Key.CursorLeft || key == (Key)('H')) { b.InvokeCommand(":seek -10"); return true; }
        if (key == Key.CursorRight || key == (Key)('L')) { b.InvokeCommand(":seek +10"); return true; }
        if (kv == 'H') { b.InvokeCommand(":seek -60"); return true; }
        if (kv == 'L') { b.InvokeCommand(":seek +60"); return true; }

        if (kv == 'g') { b.InvokeCommand(":seek 0:00"); return true; }
        if (kv == 'G') { b.InvokeCommand(":seek 100%"); return true; }

        if (key == (Key)('-')) { b.InvokeCommand(":vol -5"); return true; }
        if (key == (Key)('+')) { b.InvokeCommand(":vol +5"); return true; }

        if (key == (Key)('[')) { b.InvokeCommand(":speed -0.1"); return true; }
        if (key == (Key)(']')) { b.InvokeCommand(":speed +0.1"); return true; }
        if (key == (Key)('=')) { b.InvokeCommand(":speed 1.0"); return true; }

        // Chapter navigation, video-player convention. Only triggered when
        // the typed key is the standalone punctuation — we don't bind to
        // '<' / '>' so shift-combinations stay free for future use.
        if (key == (Key)(',')) { b.InvokeCommand(":chapter prev"); return true; }
        if (key == (Key)('.')) { b.InvokeCommand(":chapter next"); return true; }
        if (kv == '1') { b.InvokeCommand(":speed 1.0");  return true; }
        if (kv == '2') { b.InvokeCommand(":speed 1.25"); return true; }
        if (kv == '3') { b.InvokeCommand(":speed 1.5");  return true; }

        if (kv == 'd' || kv == 'D') { b.InvokeCommand(":dl toggle"); return true; }

        if (key == Key.Enter)
        {
            if (b.IsFeedsPaneActive()) { b.FocusEpisodes(); return true; }
            if (IsDetailsTabActive(b.EpisodesPane)) return true;   // Details pane: no-op
            if (IsChaptersTabActive(b.EpisodesPane)) return false;  // let ListView raise OpenSelectedItem
            b.PlaySelected();
            return true;
        }

        if (key == (Key)('n'))
        {
            var last = b.GetLastSearch();
            if (!string.IsNullOrEmpty(last))
            {
                b.ApplySearch(last!);
                b.NotifySelectedFeedChanged();
                return true;
            }
        }

        return false;
    }

    // ── episodes-pane tab helpers ────────────────────────────────────────────

    static bool IsDetailsTabActive(UiEpisodesPane? pane)
        => pane != null && pane.Tabs.SelectedTab == pane.DetailsTab;

    static bool IsChaptersTabActive(UiEpisodesPane? pane)
        => pane != null && pane.Tabs.SelectedTab == pane.ChaptersTab;

    static bool IsNonEpisodeTabActive(UiEpisodesPane? pane)
        => IsDetailsTabActive(pane) || IsChaptersTabActive(pane);

    static void SwitchToListTab(UiEpisodesPane? pane)
    {
        if (pane == null) return;
        pane.Tabs.SelectedTab = pane.EpisodesTab;
        pane.List.SetFocus();
    }

    static void SwitchToDetailsTab(UiEpisodesPane? pane)
    {
        if (pane == null) return;
        pane.Tabs.SelectedTab = pane.DetailsTab;
        pane.Details.SetFocus();
    }
}
