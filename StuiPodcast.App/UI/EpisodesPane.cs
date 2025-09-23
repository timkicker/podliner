using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal sealed class EpisodesPane
{
    public TabView Tabs { get; }
    public TabView.Tab EpisodesTab { get; }
    public TextView Details { get; }
    public ListView List { get; }
    public Label EmptyHint { get; }

    private readonly View _host;
    private readonly FrameView _detailsFrame;
    private bool _showFeedColumn;

    private List<Episode> _episodes = new();
    private List<Feed> _feeds = new();
    private Dictionary<Guid, string> _feedTitleMap = new();

    private const int FEED_COL_W = 24;
    private const string SEP = "  │  ";

    public event Action? SelectionChanged;
    public event Action? OpenSelected;

    public EpisodesPane()
    {
        Tabs = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        _host = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        List = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        EmptyHint = new Label("")
        {
            X = Pos.Center(), Y = Pos.Center(),
            AutoSize = true, Visible = false,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = Colors.Menu
        };

        List.OpenSelectedItem += _ => OpenSelected?.Invoke();
        List.SelectedItemChanged += _ => SelectionChanged?.Invoke();

        _host.Add(List);
        _host.Add(EmptyHint);

        _detailsFrame = new FrameView("Shownotes") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        Details = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        _detailsFrame.Add(Details);

        EpisodesTab = new TabView.Tab("Episodes", _host);
        Tabs.AddTab(EpisodesTab, true);
        Tabs.AddTab(new TabView.Tab("Details", _detailsFrame), false);
    }

    public void SetFeedsMeta(IEnumerable<Feed> feeds)
    {
        _feeds = (feeds ?? Enumerable.Empty<Feed>()).ToList();
        _feedTitleMap = _feeds.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Title ?? "");
    }

    public void ConfigureFeedColumn(Guid feedId, Guid vAll, Guid vSaved, Guid vDown, Guid vHist)
    {
        _showFeedColumn = (feedId == vAll || feedId == vSaved || feedId == vDown || feedId == vHist);
    }

    public void SetEpisodes(
    IEnumerable<Episode> baseEpisodes,
    Guid feedId,
    Guid FEED_ALL, Guid FEED_SAVED, Guid FEED_DOWNLOADED, Guid FEED_HISTORY,
    Func<IEnumerable<Episode>, IEnumerable<Episode>>? sorter,
    string? search,
    Guid? preferSelectId)
{
    // Scroll vor Rebuild sichern
    var keepTop = List?.TopItem ?? 0;

    // --- Filtern (virtuelle Feeds + Suche) ---
    IEnumerable<Episode> src = baseEpisodes ?? Enumerable.Empty<Episode>();

    if (feedId == FEED_SAVED)              src = src.Where(e => e.Saved);
    else if (feedId == FEED_DOWNLOADED)    src = src.Where(e => e.Downloaded);
    else if (feedId == FEED_HISTORY)       src = src.Where(e => e.LastPlayedAt != null);
    else if (feedId != FEED_ALL)           src = src.Where(e => e.FeedId == feedId);

    if (!string.IsNullOrWhiteSpace(search))
    {
        var q = search!;
        src = src.Where(e =>
            (!string.IsNullOrEmpty(e.Title)           && e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(e.DescriptionText) && e.DescriptionText.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
            (_showFeedColumn && _feedTitleMap.TryGetValue(e.FeedId, out var ft) &&
                !string.IsNullOrEmpty(ft) && ft.Contains(q, StringComparison.OrdinalIgnoreCase))
        );
    }

    // --- Sortieren ---
    if (feedId == FEED_HISTORY)
    {
        // History: zuletzt gehört zuerst; bei Gleichstand nach Fortschritt/zuletzt positioniert sortieren
        src = src
            .OrderByDescending(e => e.LastPlayedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.LastPosMs ?? 0);
    }
    else
    {
        src = (sorter != null)
            ? sorter(src)
            : src.OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue);
    }

    _episodes = src.ToList();

    // Auswahl bestimmen: bevorzugt preferSelectId, sonst aktuelle Auswahl
    int sel = 0;
    if (preferSelectId is Guid pid)
    {
        var i = _episodes.FindIndex(e => e.Id == pid);
        if (i >= 0) sel = i;
    }
    else if (List?.Source?.Count > 0)
    {
        sel = Math.Clamp(List.SelectedItem, 0, Math.Max(0, _episodes.Count - 1));
    }

    // Items bauen & setzen
    var items = _episodes.Select(RowFor).ToList();
    List.SetSource(items);
    List.SelectedItem = (items.Count > 0) ? Math.Clamp(sel, 0, items.Count - 1) : 0;

    // Scroll-Position restaurieren (sicher geclamped)
    var maxTop = Math.Max(0, items.Count - 1);
    List.TopItem = Math.Clamp(keepTop, 0, maxTop);

    UpdateEmptyHint(feedId, FEED_ALL, FEED_SAVED, FEED_DOWNLOADED, search);
}


    public Episode? GetSelected()
        => _episodes.Count == 0 ? null : _episodes[Math.Clamp(List.SelectedItem, 0, _episodes.Count - 1)];

    public void SelectIndex(int idx)
    {
        if (_episodes.Count == 0) return;
        // Nur Auswahl setzen – TopItem NICHT anfassen
        List.SelectedItem = Math.Clamp(idx, 0, _episodes.Count - 1);
    }

    public int GetSelectedIndex()
        => _episodes.Count == 0 ? 0 : Math.Clamp(List.SelectedItem, 0, _episodes.Count - 1);

    public void SetUnplayedCaption(bool on)
    {
        EpisodesTab.Text = on ? "Episodes (unplayed)" : "Episodes";
        Tabs.SetNeedsDisplay();
    }

    public void ShowDetails(Episode e)
    {
        var sb = new System.Text.StringBuilder();
        var date = e.PubDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        var title = e.Title ?? "(untitled)";
        sb.AppendLine(title);
        sb.AppendLine(new string('─', Math.Min(title.Length, 60)));
        sb.AppendLine($"Date: {date}");
        if (!string.IsNullOrWhiteSpace(e.AudioUrl))
            sb.AppendLine($"Audio: {e.AudioUrl}");
        sb.AppendLine();
        var notes = e.DescriptionText?.Trim();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes) ? "(no shownotes)" : notes);
        Details.Text = sb.ToString();
    }

    private void UpdateEmptyHint(Guid feedId, Guid vAll, Guid vSaved, Guid vDown, string? search)
    {
        bool isEmpty = (_episodes?.Count ?? 0) == 0;

        if (!isEmpty)
        {
            EmptyHint.Visible = false;
            EmptyHint.Text = "";
            Tabs.SetNeedsDisplay();
            return;
        }

        if (!string.IsNullOrWhiteSpace(search))
            EmptyHint.Text = $"No matches for “{search}”";
        else if (feedId == vSaved)
            EmptyHint.Text = "No items saved\n(:h for help)";
        else if (feedId == vDown)
            EmptyHint.Text = "No items downloaded\n(:h for help)";
        else if (feedId == vAll)
            EmptyHint.Text = "No episodes yet\nAdd one with: :add <rss-url>";
        else
            EmptyHint.Text = "No episodes in this feed";

        EmptyHint.Visible = true;
        Tabs.SetNeedsDisplay();
    }

    private string RowFor(Episode e)
    {
        // (Pfeil „▶“ wird in InjectNowPlaying injiziert)
        var nowPrefix = "  ";

        long lenMs = e.LengthMs ?? 0;
        long posMs = e.LastPosMs ?? 0;
        long effLenMs = Math.Max(lenMs, posMs);
        double r = effLenMs > 0 ? Math.Clamp((double)posMs / effLenMs, 0, 1) : 0;

        char mark = e.Played
            ? '✔'
            : r <= 0.0 ? '○'
                : r < 0.25 ? '◔'
                    : r < 0.50 ? '◑'
                        : r < 0.75 ? '◕'
                            : '●';

        var date = e.PubDate?.ToString("yyyy-MM-dd") ?? "????-??-??";
        string dur = FormatDuration(lenMs);

        char savedCh = (e.Saved == true) ? '★' : ' ';
        char downCh  = (e.Downloaded == true) ? '⬇' : ' ';
        string badges = $"{savedCh}{downCh}";

        string left = $"{nowPrefix}{mark} {date,-10}  {dur,8}  {badges}  ";

        string title = e.Title ?? "";
        int viewWidth = (List?.Bounds.Width > 0) ? List.Bounds.Width : 100;

        int reservedRight = _showFeedColumn ? (SEP.Length + FEED_COL_W) : 0;
        int availTitle = Math.Max(6, viewWidth - left.Length - reservedRight);
        string titleTrunc = TruncateTo(title, availTitle);

        if (!_showFeedColumn) return left + titleTrunc;

        string feedName = (_feedTitleMap.TryGetValue(e.FeedId, out var nm) ? nm : "") ?? "";
        string feedTrunc = TruncateTo(feedName, FEED_COL_W);
        string paddedTitle = titleTrunc.PadRight(availTitle);
        return left + paddedTitle + SEP + feedTrunc.PadRight(FEED_COL_W);
    }

    private static string TruncateTo(string? s, int max)
    {
        if (max <= 0 || string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        if (max <= 1) return "…";
        return s.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    private static string FormatDuration(long ms)
    {
        if (ms <= 0) return "--:--";
        long totalSeconds = ms / 1000;
        long h = totalSeconds / 3600;
        long m = (totalSeconds % 3600) / 60;
        long s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    public void InjectNowPlaying(Guid? nowId)
    {
        // Scroll + Selektion sichern
        int oldSel = Math.Max(0, List.SelectedItem);
        int oldTop = Math.Max(0, List.TopItem);

        var items = _episodes.Select(e =>
        {
            var row = RowFor(e);
            if (nowId != null && e.Id == nowId.Value && row.Length >= 2)
                row = "▶ " + row.Substring(2); // ersetzt die zwei führenden Spaces
            return row;
        }).ToList();

        List.SetSource(items);

        // Restore selection + scroll, geclamped
        List.SelectedItem = (items.Count > 0) ? Math.Clamp(oldSel, 0, items.Count - 1) : 0;
        int maxTop = Math.Max(0, (items.Count - 1));
        List.TopItem = Math.Clamp(oldTop, 0, maxTop);

        Tabs.SetNeedsDisplay();
    }
}
