using Serilog;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using Attribute = Terminal.Gui.Attribute;
using StuiPodcast.App.UI.Controls;

namespace StuiPodcast.App.UI;

// UiShell: list movement, focus between panes, and the layout helpers that
// back them (default theme per OS, repaint requests, the virtual-feed
// barrier row).
public sealed partial class UiShell
{
    private void MoveList(int delta)
    {
        var lv = (_activePane == Pane.Episodes) ? _episodesPane?.List : _feedsPane?.List;
        if (lv?.Source?.Count > 0)
        {
            int target = Math.Clamp(lv.SelectedItem + delta, 0, lv.Source.Count - 1);

            if (_activePane == Pane.Feeds && _feedsPane != null)
            {
                int guard = 3;
                while (guard-- > 0)
                {
                    var feed = _feeds.ElementAtOrDefault(target);
                    if (feed?.Id == VirtualFeedsCatalog.Seperator)
                        target = Math.Clamp(target + Math.Sign(delta), 0, lv.Source.Count - 1);
                    else
                        break;
                }
            }

            lv.SelectedItem = target;
            RefreshListVisual(lv);
        }
    }

    private void FocusPane(Pane p)
    {
        _activePane = p;
        if (p == Pane.Episodes)
        {
            _episodesPane?.List?.SetFocus();
            if (_episodesPane?.List is { } lv) RefreshListVisual(lv);
        }
        else
        {
            _feedsPane?.List?.SetFocus();
            if (_feedsPane?.List is { } lv2) RefreshListVisual(lv2);
        }
    }

    public void SetOfflineLookup(Func<bool> isOffline) => _episodesPane?.SetOfflineLookup(isOffline);

    private void JumpToUnplayed(int direction)
    {
        var list = _episodesPane?.List;
        var srcCount = list?.Source?.Count ?? 0;
        if (srcCount == 0 || list == null || _episodesPane == null) return;

        var current = _episodesPane.GetSelectedIndex();
        for (int step = 1; step <= srcCount; step++)
        {
            int idx = (current + step * Math.Sign(direction) + srcCount) % srcCount;

            var old = list.SelectedItem;
            list.SelectedItem = idx;
            var ep = _episodesPane.GetSelected();
            if (ep != null && !ep.ManuallyMarkedPlayed)
            {
                _episodesPane.SelectIndex(idx);
                var e = _episodesPane.GetSelected();
                if (e != null) _episodesPane.ShowDetails(e);
                return;
            }
            list.SelectedItem = old;
        }
    }

    private void SetDefaultThemeForOS()
    {
        if (OperatingSystem.IsWindows())
            _theme = ThemeMode.Base;
        else
            _theme = ThemeMode.MenuAccent;
    }

    private void RefreshListVisual(ListView lv)
    {
        try
        {
            var count = lv.Source?.Count ?? 0;
            if (count <= 0) return;

            var sel = Math.Clamp(lv.SelectedItem, 0, count - 1);
            var viewHeight = Math.Max(1, lv.Bounds.Height);
            var top = Math.Clamp(lv.TopItem, 0, Math.Max(0, count - 1));

            if (sel < top) lv.TopItem = sel;
            else if (sel >= top + viewHeight) lv.TopItem = Math.Max(0, sel - viewHeight + 1);
        }
        catch { }

        RequestRepaint();
    }

    // True when the driver knows about a terminal size the layout has not
    // picked up yet. Terminal.Gui detects resizes by polling (CursesDriver
    // .ProcessWinChange → Curses.CheckWinChange), and a missed poll leaves
    // every Toplevel rendering at the previous geometry — the crooked
    // layout from issue #4.
    public static bool NeedsRelayout()
    {
        var drv = Application.Driver;
        var top = Application.Top;
        if (drv == null || top == null) return false;

        return top.Frame.Width != drv.Cols || top.Frame.Height != drv.Rows;
    }

    // Re-syncs the layout with the driver and repaints everything. Recovers
    // from a missed resize without restarting the app. Safe to call when
    // nothing is wrong: it degrades to a plain full repaint.
    public void ForceRedraw()
    {
        UI(() =>
        {
            try
            {
                var drv = Application.Driver;
                var top = Application.Top;

                if (drv != null && top != null &&
                    (top.Frame.Width != drv.Cols || top.Frame.Height != drv.Rows))
                {
                    top.Frame = new Rect(0, 0, drv.Cols, drv.Rows);
                    top.LayoutSubviews();
                }

                RequestRepaint();
                Application.Refresh();
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "force-redraw failed");
            }
        });
    }

    private void RequestRepaint()
    {
        Application.Top?.SetNeedsDisplay();
        _mainWin?.SetNeedsDisplay();
        _rightRoot?.SetNeedsDisplay();
        _player?.SetNeedsDisplay();
        _feedsPane?.Frame?.SetNeedsDisplay();
        _feedsPane?.List?.SetNeedsDisplay();
        _episodesPane?.Tabs?.SetNeedsDisplay();
        _episodesPane?.List?.SetNeedsDisplay();
        _episodesPane?.EmptyHint?.SetNeedsDisplay();
    }

    private static IEnumerable<Feed> BuildFeedsWithBarrier(IEnumerable<Feed> realFeeds)
    {
        var virt = new List<Feed>
        {
            new Feed { Id = VirtualFeedsCatalog.All,        Title = "All Episodes" },
            new Feed { Id = VirtualFeedsCatalog.Saved,      Title = "★ Saved" },
            new Feed { Id = VirtualFeedsCatalog.Downloaded, Title = "⬇ Downloaded" },
            new Feed { Id = VirtualFeedsCatalog.Queue,      Title = "⧉ Queue" },
            new Feed { Id = VirtualFeedsCatalog.History,    Title = "⏱ History" },
        };

        var barrier = new Feed { Id = VirtualFeedsCatalog.Seperator, Title = "────────" };

        var reals = (realFeeds ?? Enumerable.Empty<Feed>()).ToList();
        return virt.Concat(new[] { barrier }).Concat(reals);
    }

    public void SetQueueOrder(IReadOnlyList<Guid> ids) => _episodesPane?.SetQueueOrder(ids);
}
