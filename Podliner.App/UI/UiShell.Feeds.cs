using Serilog;
using Terminal.Gui;
using Podliner.Core;
using Podliner.App.Debug;
using Podliner.App.Services;
using Attribute = Terminal.Gui.Attribute;
using Podliner.App.UI.Controls;

namespace Podliner.App.UI;

// UiShell: the feeds sidebar and the episode list. Split out of UiShell.cs,
// which had grown past a thousand lines; this is a file move, the members
// keep their original visibility and behaviour.
public sealed partial class UiShell
{
    public void SetFeeds(IReadOnlyList<Feed> feeds, Guid? selectId = null)
    {
        if (feeds is null) feeds = Array.Empty<Feed>();

        var viewList = BuildFeedsWithBarrier(feeds);

        _suppressFeedSelectionEvents = true;
        try
        {
            _feeds.Clear();
            _feeds.AddRange(viewList);
            _feedsPane?.SetFeeds(_feeds);

            _episodesPane?.SetFeedsMeta(feeds);

            var FEED_ALL = Guid.Parse("00000000-0000-0000-0000-00000000A11A");

            bool anyRealFeeds = feeds.Count > 0;
            Guid want = selectId
                        ?? (anyRealFeeds
                            ? _feeds.FirstOrDefault(f => f.Id != VirtualFeedsCatalog.Seperator)?.Id ?? FEED_ALL
                            : FEED_ALL);

            int idx = 0;
            var j = _feeds.FindIndex(f => f.Id == want);
            if (j >= 0) idx = j;

            if (_feeds.ElementAtOrDefault(idx)?.Id == VirtualFeedsCatalog.Seperator)
            {
                idx = Math.Clamp(idx + 1, 0, Math.Max(0, _feeds.Count - 1));
                if (_feeds.ElementAtOrDefault(idx)?.Id == VirtualFeedsCatalog.Seperator)
                    idx = 0;
            }

            if (_feeds.Count == 0) idx = 0;

            _feedsPane?.List?.SetSelectedItemIfPresent(idx);
        }
        finally
        {
            _suppressFeedSelectionEvents = false;
        }

        RefreshEpisodesForSelectedFeed(_episodes);
    }

    public Guid? GetSelectedFeedId()
    {
        var id = _feedsPane?.GetSelectedFeedId();
        return (id == VirtualFeedsCatalog.Seperator) ? (Guid?)null : id;
    }

    public void SelectFeed(Guid id) => _feedsPane?.SelectFeed(id);

    public void RefreshEpisodesForSelectedFeed(IEnumerable<Episode> episodes)
    {
        var fid = GetSelectedFeedId();
        if (fid is Guid id) SetEpisodesForFeed(id, episodes);
    }

    public void SetEpisodesForFeed(Guid feedId, IEnumerable<Episode> episodes)
    {
        UI(() =>
        {
            if (_episodesPane == null) return;

            _episodesPane.ConfigureFeedColumn(feedId, VirtualFeedsCatalog.All, VirtualFeedsCatalog.Saved, VirtualFeedsCatalog.Downloaded, VirtualFeedsCatalog.History, VirtualFeedsCatalog.Queue);

            var prevId  = _episodesPane.GetSelected()?.Id;
            var keepTop = _episodesPane.List?.TopItem ?? 0;

            _episodesPane.SetEpisodes(
                episodes, feedId,
                VirtualFeedsCatalog.All, VirtualFeedsCatalog.Saved, VirtualFeedsCatalog.Downloaded, VirtualFeedsCatalog.History, VirtualFeedsCatalog.Queue,
                EpisodeSorter, _lastSearch, prevId
            );

            _episodesPane.InjectNowPlaying(_nowPlayingId);

            if (_episodesPane.List?.Source is IList<object> src)
            {
                var maxTop = Math.Max(0, src.Count - 1);
                _episodesPane.List.TopItem = Math.Clamp(keepTop, 0, maxTop);
            }
        });
    }
    
    
    public void RequestRemoveFeed()
    {
        try { RemoveFeedRequested?.Invoke(); } catch { }
    }

    public Episode? GetSelectedEpisode() => _episodesPane?.GetSelected();
    public int GetSelectedEpisodeIndex() => _episodesPane?.GetSelectedIndex() ?? -1;

    public void SelectEpisodeIndex(int index)
    {
        UI(() =>
        {
            if (_episodesPane == null) return;
            _episodesPane.SelectIndex(index);
            var ep = _episodesPane.GetSelected();
            if (ep != null) _episodesPane.ShowDetails(ep);
            if (_episodesPane.List is { } lv) RefreshListVisual(lv);
        });
    }

    public void SetUnplayedHint(bool on) => _episodesPane?.SetUnplayedCaption(on);

    // Forward a fresh playback snapshot to the episodes pane so the active row
    // shows live progress without triggering a full list rebuild.
    public void RefreshActiveProgress(PlaybackSnapshot snap)
        => UI(() => _episodesPane?.RefreshActiveProgress(snap));

    public void SetNowPlaying(Guid? episodeId)
    {
        UI(() =>
        {
            _nowPlayingId = episodeId;
            _episodesPane?.InjectNowPlaying(_nowPlayingId);
        });
    }

    public void ShowStartupEpisode(Episode ep, int? volume = null, double? speed = null)
    {
        UI(() =>
        {
            _startupPinned = true;
            _nowPlayingId = ep.Id;

            SetWindowTitle(ep.Title);
            long len = ep.DurationMs;
            long pos = ep.Progress.LastPosMs;
            if (_player != null)
                _player.Progress.Fraction = (len > 0) ? Math.Clamp((float)pos / len, 0f, 1f) : 0f;

            var lenTs = TimeSpan.FromMilliseconds(Math.Max(0, len));
            var posTs = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(pos, len)));
            var posStr = UIGlyphSet.FormatTime(posTs);
            var lenStr = len == 0 ? "--:--" : UIGlyphSet.FormatTime(lenTs);
            var remStr = len == 0 ? "--:--" : UIGlyphSet.FormatTime(lenTs - posTs);
            _player?.TimeLabel?.SetText($"⏸ {posStr} / {lenStr}  (-{remStr})");

            // The persisted volume and speed have no snapshot to ride in on
            // yet, so paint them here; otherwise the bar reads 0% until the
            // user touches something.
            _player?.SetVolumeAndSpeed(volume, speed);

            _episodesPane?.InjectNowPlaying(_nowPlayingId);
        });
    }
}
