using StuiPodcast.Core;
using Terminal.Gui;

namespace StuiPodcast.App.UI.Controls;

// Chapters tab content. Shows time + title per row; highlights the active
// chapter when the selected episode is currently playing. Stateless beyond
// its own cached row strings — the parent pane decides when to reload.
internal sealed class UiChaptersList
{
    public ListView List { get; }
    public Label Placeholder { get; }

    readonly View _host;
    List<Chapter> _chapters = new();
    int _activeIdx = -1;
    string? _placeholderText;

    public event Action? OpenSelected;
    public event Action? SelectionChanged;

    public UiChaptersList()
    {
        _host = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        List = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        Placeholder = new Label("")
        {
            X = Pos.Center(), Y = Pos.Center(),
            AutoSize = true, Visible = false,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = Colors.Menu
        };
        List.OpenSelectedItem += _ => OpenSelected?.Invoke();
        List.SelectedItemChanged += _ => SelectionChanged?.Invoke();
        _host.Add(List);
        _host.Add(Placeholder);
    }

    public View Host => _host;

    public int Count => _chapters.Count;
    public IReadOnlyList<Chapter> Chapters => _chapters;
    public Chapter? GetSelected()
    {
        if (_chapters.Count == 0) return null;
        return _chapters[Math.Clamp(List.SelectedItem, 0, _chapters.Count - 1)];
    }
    public int GetSelectedIndex() => _chapters.Count == 0 ? -1 : Math.Clamp(List.SelectedItem, 0, _chapters.Count - 1);

    // Replace the visible rows. activeIndex is the live-playing chapter
    // or -1 to suppress the highlight (e.g. when the selected episode is
    // not the one playing).
    public void SetChapters(IReadOnlyList<Chapter> chapters, int activeIndex = -1)
    {
        _chapters = (chapters ?? (IReadOnlyList<Chapter>)Array.Empty<Chapter>()).ToList();
        _activeIdx = _chapters.Count == 0 ? -1 : (activeIndex < 0 || activeIndex >= _chapters.Count ? -1 : activeIndex);

        if (_chapters.Count == 0)
        {
            List.SetSource(Array.Empty<string>());
            ShowPlaceholder(_placeholderText ?? "No chapters for this episode");
            return;
        }

        Placeholder.Visible = false;

        bool anyOverHour = _chapters[^1].StartSeconds >= 3600;
        int timeWidth = anyOverHour ? 9 : 6; // "h:mm:ss" or "mm:ss"
        var rows = new List<string>(_chapters.Count);
        for (int i = 0; i < _chapters.Count; i++)
            rows.Add(FormatRow(_chapters[i], i, _activeIdx, timeWidth));

        List.SetSource(rows);
        List.SelectedItem = 0;
    }

    // Adjust only the live highlight without rebuilding the list. Avoids
    // jitter when the 4-Hz playback snapshot pulses several times per second.
    public void SetActiveIndex(int index)
    {
        if (_chapters.Count == 0) return;
        int next = index < 0 || index >= _chapters.Count ? -1 : index;
        if (next == _activeIdx) return;
        _activeIdx = next;

        bool anyOverHour = _chapters[^1].StartSeconds >= 3600;
        int timeWidth = anyOverHour ? 9 : 6;
        var rows = new List<string>(_chapters.Count);
        for (int i = 0; i < _chapters.Count; i++)
            rows.Add(FormatRow(_chapters[i], i, _activeIdx, timeWidth));
        var keepSel = Math.Clamp(List.SelectedItem, 0, _chapters.Count - 1);
        var keepTop = List.TopItem;
        List.SetSource(rows);
        List.SelectedItem = keepSel;
        List.TopItem = Math.Clamp(keepTop, 0, Math.Max(0, _chapters.Count - 1));
    }

    // Show a message instead of a list — used for "Loading…", "Select an
    // episode", offline, and engine-doesn't-support-seek states.
    public void ShowPlaceholder(string text)
    {
        _chapters = new();
        _activeIdx = -1;
        _placeholderText = text;
        List.SetSource(Array.Empty<string>());
        Placeholder.Text = string.IsNullOrWhiteSpace(text) ? "" : text;
        Placeholder.Visible = !string.IsNullOrWhiteSpace(text);
    }

    static string FormatRow(Chapter c, int idx, int activeIdx, int timeWidth)
    {
        var prefix = idx == activeIdx ? "▶ " : "  ";
        var time = FormatTime(c.StartSeconds).PadLeft(timeWidth);
        var title = string.IsNullOrWhiteSpace(c.Title) ? "(untitled)" : c.Title;
        return $"{prefix}{time}  {title}";
    }

    // "5:11" or "1:23:45". Negative or NaN → "0:00" so we never crash the
    // render on malformed input.
    internal static string FormatTime(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || totalSeconds < 0) totalSeconds = 0;
        int total = (int)totalSeconds;
        int h = total / 3600;
        int m = (total % 3600) / 60;
        int s = total % 60;
        return h > 0
            ? $"{h}:{m:00}:{s:00}"
            : $"{m}:{s:00}";
    }

    // Current chapter for the given playback position. -1 if position is
    // before the first chapter or the list is empty.
    public static int IndexForPosition(IReadOnlyList<Chapter> chapters, double posSeconds)
    {
        if (chapters == null || chapters.Count == 0) return -1;
        int last = -1;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].StartSeconds <= posSeconds) last = i;
            else break;
        }
        return last;
    }
}
