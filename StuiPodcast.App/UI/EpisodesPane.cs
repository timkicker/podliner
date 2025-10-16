using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal sealed class EpisodesPane
{
    private int _historyLimit = 200;
    public void SetHistoryLimit(int n) => _historyLimit = Math.Clamp(n, 10, 10000);

    private Func<Guid, DownloadState>? _dlStateLookup;
    private Func<bool>? _isOffline;

    public void SetOfflineLookup(Func<bool> fn) => _isOffline = fn;

    // UI (werden im Ctor initialisiert; non-null)
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

    private Func<Guid, bool>? _isQueued;             // Badge-Lookup
    private readonly List<Guid> _queueOrder = new(); // Reihenfolge für Queue-Tab

    // Snapshot für die *aktive* Episode (synchroner Progress)
    private Guid? _nowPlayingId;
    private PlaybackSnapshot? _activeSnapshot; // nur für _nowPlayingId relevant

    public void SetDownloadStateLookup(Func<Guid, DownloadState> fn)
        => _dlStateLookup = fn ?? (_ => DownloadState.None);

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
        // Robust gegen doppelte IDs und null-Titel
        _feedTitleMap = _feeds
            .GroupBy(f => f.Id)
            .ToDictionary(g => g.Key, g => g.First().Title ?? string.Empty);
    }

    public void SetQueueLookup(Func<Guid, bool> isQueued) => _isQueued = isQueued ?? (_ => false);

    public void SetQueueOrder(IReadOnlyList<Guid> ids)
    {
        _queueOrder.Clear();
        if (ids != null) _queueOrder.AddRange(ids);
    }

    public void ConfigureFeedColumn(Guid feedId, Guid vAll, Guid vSaved, Guid vDown, Guid vHistory, Guid vQueue)
    {
        // In All/Saved/Downloaded/History/Queue wollen wir die Feed-Spalte zeigen.
        _showFeedColumn = (feedId == vAll || feedId == vSaved || feedId == vDown || feedId == vHistory || feedId == vQueue);
    }

    public void SetEpisodes(
        IEnumerable<Episode> baseEpisodes,
        Guid feedId,
        Guid FEED_ALL, Guid FEED_SAVED, Guid FEED_DOWNLOADED, Guid FEED_HISTORY, Guid FEED_QUEUE,
        Func<IEnumerable<Episode>, IEnumerable<Episode>>? sorter,
        string? search,
        Guid? preferSelectId)
    {
        // Auswahl & Scroll vor Rebuild sichern
        var keepTop = List.TopItem;
        var keepSel = (List.Source?.Count > 0) ? Math.Clamp(List.SelectedItem, 0, List.Source.Count - 1) : 0;

        IEnumerable<Episode> src = baseEpisodes ?? Enumerable.Empty<Episode>();

        if (feedId == FEED_HISTORY)
        {
            // HISTORY: strikt nach LastPlayedAt desc, Sort & Search bewusst ignorieren
            src = src.Where(e => e.LastPlayedAt != null)
                     .OrderByDescending(e => e.LastPlayedAt ?? DateTimeOffset.MinValue)
                     .Take(_historyLimit);
        }
        else if (feedId == FEED_QUEUE)
        {
            // QUEUE: FIFO anhand _queueOrder, Suche/Sort bewusst ignoriert
            _showFeedColumn = true; // Queue listet versch. Feeds → Spalte macht Sinn
            var map = src.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            src = _queueOrder.Where(map.ContainsKey).Select(id => map[id]);
        }
        else
        {
            // 1) Feed-Filter
            if (feedId == FEED_SAVED)             src = src.Where(e => e.Saved);
            else if (feedId == FEED_DOWNLOADED)   src = src.Where(e => e.Downloaded);
            else if (feedId != FEED_ALL)          src = src.Where(e => e.FeedId == feedId);

            // 2) Suche
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

            // 3) Sort – GANZ AM ENDE (falls gesetzt), sonst Default pubdate desc
            src = (sorter != null)
                ? sorter(src)
                : src.OrderByDescending(e => e.PubDate ?? DateTimeOffset.MinValue);
        }

        _episodes = src.ToList();

        // Zielauswahl bestimmen
        int sel = 0;
        if (preferSelectId is Guid pid)
        {
            var i = _episodes.FindIndex(e => e.Id == pid);
            if (i >= 0) sel = i;
        }
        else
        {
            sel = Math.Clamp(keepSel, 0, Math.Max(0, _episodes.Count - 1));
        }

        // Items setzen
        var items = _episodes.Select(e => RowFor(e, _nowPlayingId, _activeSnapshot)).ToList();
        List.SetSource(items);
        List.SelectedItem = (items.Count > 0) ? Math.Clamp(sel, 0, items.Count - 1) : 0;

        // Scroll wiederherstellen
        var maxTop = Math.Max(0, items.Count - 1);
        List.TopItem = Math.Clamp(keepTop, 0, maxTop);

        UpdateEmptyHint(feedId, FEED_ALL, FEED_SAVED, FEED_DOWNLOADED, FEED_HISTORY, FEED_QUEUE, search);
    }

    public Episode? GetSelected()
        => _episodes.Count == 0 ? null : _episodes[Math.Clamp(List.SelectedItem, 0, _episodes.Count - 1)];

    public void SelectIndex(int index)
    {
        if (List.Source?.Count > 0)
        {
            List.SelectedItem = Math.Clamp(index, 0, List.Source.Count - 1);
        }
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
        if (e.LastPlayedAt != null)
            sb.AppendLine($"Last played: {e.LastPlayedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        var notes = e.DescriptionText?.Trim();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes) ? "(no shownotes)" : notes);
        Details.Text = sb.ToString();
    }

    private void UpdateEmptyHint(Guid feedId, Guid vAll, Guid vSaved, Guid vDown, Guid vHist, Guid vQueue, string? search)
    {
        bool isEmpty = (_episodes?.Count ?? 0) == 0;

        if (!isEmpty)
        {
            EmptyHint.Visible = false;
            EmptyHint.Text = "";
            Tabs.SetNeedsDisplay();
            return;
        }

        if (feedId == vQueue)
        {
            EmptyHint.Text = "Queue is empty\n(:queue add / q)";
        }
        else if (!string.IsNullOrWhiteSpace(search))
        {
            EmptyHint.Text = $"No matches for “{search}”";
        }
        else if (feedId == vSaved)
        {
            EmptyHint.Text = "No items saved\n(:h for help)";
        }
        else if (feedId == vDown)
        {
            EmptyHint.Text = "No items downloaded\n(:h for help)";
        }
        else if (feedId == vHist)
        {
            EmptyHint.Text = "No listening history yet";
        }
        else if (feedId == vAll)
        {
            EmptyHint.Text = "No episodes yet\nAdd one with: :add <rss-url>";
        }
        else
        {
            EmptyHint.Text = "No episodes in this feed";
        }

        EmptyHint.Visible = true;
        Tabs.SetNeedsDisplay();
    }

    // --- Rendering ----------------------------------------------------------------

    // Fortschritt & Badges aus persistenten Feldern,
    // aber für die *aktive* Episode (nowId) optional mit Snapshot-Override.
    private string RowFor(Episode e, Guid? nowId, PlaybackSnapshot? snapForActive)
    {
        // Prefix: neutrales "  " oder "▶ " für aktive Episode
        bool isNow = nowId != null && e.Id == nowId.Value;
        var nowPrefix = GlyphSet.NowPrefix(isNow);

        // Länge/Position – Default aus Persistenz
        long lenMs = e.LengthMs ?? 0;
        long posMs = e.LastPosMs ?? 0;

        // Snapshot-Override NUR für aktive Episode anwenden (synchron mit Player)
        if (isNow && snapForActive is PlaybackSnapshot snap)
        {
            var sp = (long)Math.Max(0, snap.Position.TotalMilliseconds);
            var sl = (long)Math.Max(0, snap.Length.TotalMilliseconds);
            // Eff-> nie kleiner als Pos
            lenMs = Math.Max(lenMs, sl);
            posMs = Math.Max(0, Math.Min(sp, Math.Max(1, lenMs)));
        }

        long effLenMs = Math.Max(lenMs, posMs);
        double r = effLenMs > 0 ? Math.Clamp((double)posMs / effLenMs, 0, 1) : 0;

        char mark = GlyphSet.ProgressGlyph(r, e.Played);

        var date = e.PubDate?.ToString("yyyy-MM-dd") ?? "????-??-??";
        string dur = GlyphSet.FormatDuration(lenMs);

        // Download-Zustand / Badges
        var ds = _dlStateLookup?.Invoke(e.Id) ?? DownloadState.None;

        bool offline = _isOffline?.Invoke() == true;
        bool hasLocal = (ds == DownloadState.Done) || e.Downloaded;

        string badges = GlyphSet.ComposeBadges(
            isSaved: e.Saved,
            dlState: ds,
            isQueued: _isQueued?.Invoke(e.Id) == true,
            showOffline: offline && !hasLocal
        );

        string left = $"{nowPrefix}{mark} {date,-10}  {dur,8}  {badges}  ";

        string title = e.Title ?? string.Empty;
        int viewWidth = (List.Bounds.Width > 0) ? List.Bounds.Width : 100;

        int reservedRight = _showFeedColumn ? (GlyphSet.Separator.Length + FEED_COL_W) : 0;
        int availTitle = Math.Max(6, viewWidth - left.Length - reservedRight);
        string titleTrunc = TruncateTo(title, availTitle);

        if (!_showFeedColumn) return left + titleTrunc;

        string feedName = (_feedTitleMap.TryGetValue(e.FeedId, out var nm) ? nm : string.Empty) ?? string.Empty;
        string feedTrunc = TruncateTo(feedName, FEED_COL_W);
        string paddedTitle = titleTrunc.PadRight(availTitle);
        return left + paddedTitle + GlyphSet.Separator + feedTrunc.PadRight(FEED_COL_W);
    }

    private static string TruncateTo(string? s, int max)
    {
        if (max <= 0 || string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        if (max <= 1) return "…";
        return s.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    // --- API: NowPlaying-Markierung ------------------------------------------------

    // Bestehende Signatur bleibt (Kompatibilität)
    public void InjectNowPlaying(Guid? nowId)
    {
        _nowPlayingId = nowId;
        _activeSnapshot = null; // kein Snapshot → persistente Werte
        RebuildRowsPreservingView();
    }

    // Overload mit Snapshot – synchronisiert Fortschritt der aktiven Episode zur Player-Anzeige
    public void InjectNowPlaying(Guid? nowId, PlaybackSnapshot snapshot)
    {
        _nowPlayingId = nowId;
        _activeSnapshot = snapshot;
        RebuildRowsPreservingView();
    }

    private void RebuildRowsPreservingView()
    {
        var items = _episodes.Select(e => RowFor(e, _nowPlayingId, _activeSnapshot)).ToList();

        // Auswahl & Scroll beibehalten
        var keepSel = (List.Source?.Count > 0) ? Math.Clamp(List.SelectedItem, 0, List.Source.Count - 1) : 0;
        var keepTop = List.TopItem;

        List.SetSource(items);
        var maxSel = Math.Max(0, items.Count - 1);
        List.SelectedItem = Math.Clamp(keepSel, 0, maxSel);
        List.TopItem = Math.Clamp(keepTop, 0, Math.Max(0, items.Count - 1));

        Tabs.SetNeedsDisplay();
    }
}
