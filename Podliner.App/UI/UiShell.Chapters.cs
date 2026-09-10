using Serilog;
using Terminal.Gui;
using Podliner.Core;
using Podliner.App.Debug;
using Podliner.App.Services;
using Attribute = Terminal.Gui.Attribute;
using Podliner.App.UI.Controls;

namespace Podliner.App.UI;

// UiShell: the Chapters tab. The loader lives outside the shell (wired in
// Program.cs to ChaptersUseCase); these methods only push results in and
// drop anything that arrives for an episode the tab no longer shows.
public sealed partial class UiShell
{
    public void SetChaptersLoading(string message) => UI(() =>
    {
        _episodesPane?.ChaptersList.ShowPlaceholder(message);
        _episodesPane?.SetChaptersTabCount(null);
    });

    public void SetChaptersResult(Guid episodeId, IReadOnlyList<Chapter> chapters, int activeIndex = -1) => UI(() =>
    {
        // Stale-guard: the user may have switched episodes while the fetch
        // was in flight. Drop the result if we're no longer showing that ep.
        if (_chaptersActiveEpisodeId != episodeId) return;
        if (_episodesPane == null) return;
        _episodesPane.ChaptersList.SetChapters(chapters, activeIndex);
        _episodesPane.SetChaptersTabCount(chapters.Count);
    });

    public void SetChaptersEmpty(Guid episodeId, string message) => UI(() =>
    {
        if (_chaptersActiveEpisodeId != episodeId) return;
        if (_episodesPane == null) return;
        _episodesPane.ChaptersList.ShowPlaceholder(message);
        _episodesPane.SetChaptersTabCount(0);
    });

    public void UpdateChapterHighlight(Guid episodeId, double posSeconds) => UI(() =>
    {
        if (_chaptersActiveEpisodeId != episodeId) return;
        if (_episodesPane == null) return;
        var chapters = _episodesPane.ChaptersList.Chapters;
        if (chapters.Count == 0) return;
        var idx = UiChaptersList.IndexForPosition(chapters, posSeconds);
        _episodesPane.ChaptersList.SetActiveIndex(idx);
    });
}
