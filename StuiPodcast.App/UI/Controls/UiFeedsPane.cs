using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI.Controls;

internal sealed class UiFeedsPane
{
    #region ui elements

    public FrameView Frame { get; }
    public ListView  List  { get; }

    #endregion

    #region state

    private List<Feed> _feeds = new();
    private List<string> _rows = new();

    #endregion

    #region events

    public event Action? SelectedChanged;
    public event Action? OpenRequested;

    #endregion

    #region lifecycle

    public UiFeedsPane()
    {
        Frame = new FrameView("Feeds") { X = 0, Y = 0, Width = 30, Height = Dim.Fill() };
        List  = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        List.OpenSelectedItem += _ => OpenRequested?.Invoke();
        List.SelectedItemChanged += _ => SelectedChanged?.Invoke();

        if (Application.Top != null)
            Application.Top.Resized += _ => RebuildRowsAndRefresh(preserveScroll: true);

        Frame.Add(List);
    }

    #endregion

    #region public api

    public void SetFeeds(IEnumerable<Feed> feeds)
    {
        // preserve selection and scroll when rebuilding rows
        var keepTop = List.TopItem;
        var keepSel = List.Source?.Count > 0 ? Math.Clamp(List.SelectedItem, 0, List.Source.Count - 1) : 0;

        _feeds = (feeds ?? Enumerable.Empty<Feed>()).ToList();

        RebuildRows();

        List.SetSource(_rows);
        List.SelectedItem = _rows.Count > 0 ? Math.Clamp(keepSel, 0, _rows.Count - 1) : 0;
        List.TopItem = Math.Clamp(keepTop, 0, Math.Max(0, _rows.Count - 1));
    }

    public Guid? GetSelectedFeedId()
    {
        if (_feeds.Count == 0 || List.Source is null) return null;
        int i = Math.Clamp(List.SelectedItem, 0, _feeds.Count - 1);
        return _feeds.ElementAtOrDefault(i)?.Id;
    }

    public void SelectFeed(Guid id)
    {
        if (_feeds.Count == 0) return;
        var idx = _feeds.FindIndex(f => f.Id == id);
        if (idx >= 0)
        {
            List.SelectedItem = idx;
            EnsureSelectionVisible(List);
        }
    }

    public IReadOnlyList<Feed> RawFeeds => _feeds;

    #endregion

    #region internals

    private void RebuildRowsAndRefresh(bool preserveScroll)
    {
        int keepTop = preserveScroll ? List.TopItem : 0;
        int keepSel = preserveScroll && List.Source?.Count > 0 ? Math.Clamp(List.SelectedItem, 0, List.Source.Count - 1) : 0;

        RebuildRows();

        List.SetSource(_rows);
        if (preserveScroll)
        {
            List.SelectedItem = _rows.Count > 0 ? Math.Clamp(keepSel, 0, _rows.Count - 1) : 0;
            List.TopItem = Math.Clamp(keepTop, 0, Math.Max(0, _rows.Count - 1));
        }
    }

    private void RebuildRows()
    {
        int viewWidth = Math.Max(
            4,
            ((List.Bounds.Width > 0 ? List.Bounds.Width : 0) != 0
                ? List.Bounds.Width
                : (Frame.Bounds.Width > 0 ? Frame.Bounds.Width : 30)) - 2);

        _rows = _feeds
            .Select(f => TruncateTo(f?.Title ?? string.Empty, viewWidth))
            .ToList();
    }

    private static string TruncateTo(string? s, int max)
    {
        if (max <= 0 || string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        if (max <= 1) return "…";
        return s[..Math.Max(0, max - 1)] + "…";
    }

    private static void EnsureSelectionVisible(ListView lv)
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
    }

    #endregion
}
