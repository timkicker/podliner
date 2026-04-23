using Serilog;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Feeds;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command.UseCases;

// :chapter list | next | prev | jump <n>
// Works on the currently-playing or selected episode. Lazy-loads the
// chapters JSON on first use via ChaptersFetcher, then seeks via the
// audio player. Prev jumps to the start of the current chapter if we're
// >3s in (like mpv/VLC), else the previous chapter.
internal sealed class ChaptersUseCase
{
    readonly IUiShell _ui;
    readonly IAudioPlayer _audioPlayer;
    readonly IEpisodeStore _episodes;
    readonly ChaptersFetcher _fetcher;
    readonly Func<Task> _persist;
    // Resolves a downloaded episode's local path, or returns null if the
    // episode isn't on disk yet. Lets us read ID3 chapters without a
    // network round-trip when the user has already downloaded the episode.
    readonly Func<Guid, string?>? _localPathLookup;

    // Session-local negative cache. An episode for which all three fallback
    // paths (RSS, local-id3, remote-id3) turned up nothing. Without this, a
    // user who types :chapter on an unchaptered episode repeatedly would
    // download 2MB via HTTP Range every time.
    readonly HashSet<Guid> _negative = new();

    // Network-online probe for the UI path. Returns null by default so the
    // command path (which already runs even offline) isn't affected.
    public Func<bool>? IsOnlineLookup { get; set; }

    public ChaptersUseCase(IUiShell ui, IAudioPlayer audioPlayer, IEpisodeStore episodes,
        ChaptersFetcher fetcher, Func<Task> persist, Func<Guid, string?>? localPathLookup = null)
    {
        _ui              = ui;
        _audioPlayer     = audioPlayer;
        _episodes        = episodes;
        _fetcher         = fetcher;
        _persist         = persist;
        _localPathLookup = localPathLookup;
    }

    public void Exec(string[] args)
    {
        var sub = args.Length == 0 ? "list" : args[0].Trim().ToLowerInvariant();

        // Diagnostic: ":chapter probe <url>" — fetch + parse a raw URL with
        // no episode context. Lets us prove the full pipeline works even
        // when the user hasn't selected a chapter-having episode.
        if (sub == "probe")
        {
            if (args.Length < 2)
            {
                _ui.ShowOsd("usage: :chapter probe <audio-url>", 2000);
                return;
            }
            var url = args[1];
            _ui.ShowOsd($"chapters probe: fetching…", 1500);
            _ = Task.Run(async () =>
            {
                var list = await _fetcher.FetchFromUrlId3Async(url);
                var msg = list is null
                    ? "chapters probe: null (parse failed / no tag)"
                    : $"chapters probe: {list.Count} chapter(s)";
                _ui.ShowOsd(msg, 3000);
                Log.Information("chapters/probe url={Url} count={Count}", url, list?.Count ?? -1);
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        Log.Information("  [{Idx}] {Sec:0.00}s  {Title}", i + 1, list[i].StartSeconds, list[i].Title);
            });
            return;
        }

        var ep = ResolveEpisode();
        if (ep == null) { _ui.ShowOsd("chapters: no episode selected or playing", 1800); return; }

        // Log what we're about to operate on so the user can correlate
        // with what they see in the UI. Useful when "chapters: none" fires
        // — helps distinguish "wrong episode selected" from "no chapters".
        Log.Information("chapters/resolve ep={Title} url={Url}", ep.Title, ep.AudioUrl);

        // Lazy-load JSON the first time. Each sub-command awaits the load
        // before dispatching; subsequent calls hit the in-memory cache.
        _ = Task.Run(async () =>
        {
            var loaded = await EnsureLoadedAsync(ep);
            if (!loaded)
            {
                // Surface which episode was checked so the user can tell
                // whether they pointed at the right one — the difference
                // between "wrong episode selected" and "episode genuinely
                // has no chapters" was otherwise invisible.
                var shortTitle = (ep.Title ?? "?");
                if (shortTitle.Length > 40) shortTitle = shortTitle[..40] + "…";
                _ui.ShowOsd($"chapters: none for '{shortTitle}'", 2000);
                return;
            }

            switch (sub)
            {
                case "list":  ShowList(ep); break;
                case "next":  Jump(ep, +1); break;
                case "prev":  Jump(ep, -1); break;
                case "jump":
                    var idx = args.Length >= 2 && int.TryParse(args[1], out var n) ? n : -1;
                    JumpTo(ep, idx);
                    break;
                default:
                    _ui.ShowOsd("usage: :chapter list|next|prev|jump <n>", 1500);
                    break;
            }
        });
    }

    // Resolution chain:
    //   1. Already cached on the Episode (any previous load).
    //   2. podcast:chapters RSS extension (JSON URL). Spec-clean but rare.
    //   3. ID3 CHAP frames from the downloaded file. Most common in the wild.
    //   4. ID3 CHAP frames via HTTP Range on the audio URL (first 2MB).
    // Any successful step persists onto the episode so repeats are O(1).
    async Task<bool> EnsureLoadedAsync(Episode ep)
    {
        if (ep.Chapters.Count > 0) return true;
        if (_negative.Contains(ep.Id)) return false;

        List<Chapter>? list = null;

        // Step 2: RSS podcast:chapters JSON URL.
        if (!string.IsNullOrWhiteSpace(ep.ChaptersUrl))
        {
            Log.Debug("chapters/try rss url={Url}", ep.ChaptersUrl);
            list = await _fetcher.FetchAsync(ep.ChaptersUrl);
        }

        // Step 3: local downloaded file → ID3 CHAP.
        if ((list is null || list.Count == 0) && _localPathLookup != null)
        {
            var path = _localPathLookup(ep.Id);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Log.Debug("chapters/try local-id3 path={Path}", path);
                list = ChaptersFetcher.FetchFromLocalFile(path);
            }
        }

        // Step 4: HTTP Range on AudioUrl → ID3 CHAP from the first 2MB.
        if ((list is null || list.Count == 0) && !string.IsNullOrWhiteSpace(ep.AudioUrl))
        {
            Log.Debug("chapters/try remote-id3 url={Url}", ep.AudioUrl);
            list = await _fetcher.FetchFromUrlId3Async(ep.AudioUrl);
        }

        if (list is null || list.Count == 0)
        {
            Log.Information("chapters/none id={Id} title={Title}", ep.Id, ep.Title);
            _negative.Add(ep.Id);
            return false;
        }

        Log.Information("chapters/loaded id={Id} count={Count}", ep.Id, list.Count);
        ep.Chapters = list;
        try { _ = _persist(); }
        catch (Exception ex) { Log.Debug(ex, "chapters persist-after-load failed id={Id}", ep.Id); }
        return true;
    }

    void ShowList(Episode ep)
    {
        var cur = CurrentChapterIndex(ep);
        var msg = $"chapters: {ep.Chapters.Count} total" +
                  (cur >= 0 ? $" — current: {cur + 1}. {ep.Chapters[cur].Title}" : "");
        _ui.ShowOsd(msg, 2500);
    }

    void Jump(Episode ep, int step)
    {
        var cur = CurrentChapterIndex(ep);
        int target;
        if (step < 0)
        {
            // mpv-style: prev jumps to start of current chapter if we're
            // past a small grace window, otherwise to the previous chapter.
            var posMs = _audioPlayer.State?.Position.TotalMilliseconds ?? 0;
            var curStartMs = cur >= 0 ? ep.Chapters[cur].StartSeconds * 1000 : 0;
            if (cur >= 0 && posMs - curStartMs > 3000) target = cur;
            else                                        target = Math.Max(0, cur - 1);
        }
        else
        {
            target = Math.Min(ep.Chapters.Count - 1, Math.Max(0, cur) + 1);
        }
        JumpTo(ep, target + 1);
    }

    void JumpTo(Episode ep, int oneBased)
    {
        if (oneBased < 1 || oneBased > ep.Chapters.Count)
        {
            _ui.ShowOsd($"chapters: index out of range (1–{ep.Chapters.Count})", 1800);
            return;
        }
        var ch = ep.Chapters[oneBased - 1];
        try { _audioPlayer.SeekTo(TimeSpan.FromSeconds(ch.StartSeconds)); }
        catch (Exception ex) { Log.Debug(ex, "chapters seek failed idx={Idx}", oneBased); }
        _ui.ShowOsd($"▶ {oneBased}. {ch.Title}", 1500);
    }

    // Index of the chapter containing the current playback position, or -1
    // if none / no playback. We rely on ascending sort done by ChaptersFetcher.
    int CurrentChapterIndex(Episode ep)
    {
        if (ep.Chapters.Count == 0) return -1;
        var posSec = (_audioPlayer.State?.Position.TotalSeconds ?? 0);
        int last = -1;
        for (int i = 0; i < ep.Chapters.Count; i++)
        {
            if (ep.Chapters[i].StartSeconds <= posSec) last = i;
            else break;
        }
        return last;
    }

    // Result shape for the UI path. Outcome lets the pane distinguish
    // "show list" from "show empty state" from "show offline hint".
    public enum LoadOutcome { Loaded, NoChapters, Offline, NoSource, Error }
    public readonly record struct UiLoadResult(LoadOutcome Outcome, IReadOnlyList<Chapter> Chapters);

    // Public entry for the Chapters tab. Mirrors Exec()'s loader but
    // returns a structured outcome for rendering instead of OSDing. Runs
    // on a worker thread; the caller is expected to marshal onto MainLoop
    // before touching Views.
    public async Task<UiLoadResult> LoadForUiAsync(Episode ep)
    {
        if (ep == null) return new UiLoadResult(LoadOutcome.NoSource, Array.Empty<Chapter>());

        if (ep.Chapters.Count > 0)
            return new UiLoadResult(LoadOutcome.Loaded, ep.Chapters);

        // Offline gate. Only blocks when remote fetch is the only option —
        // if we've got a local downloaded file, ID3 parse is still possible.
        bool online = IsOnlineLookup?.Invoke() ?? true;
        bool hasLocal = _localPathLookup?.Invoke(ep.Id) is { Length: > 0 } path && File.Exists(path);
        if (!online && !hasLocal && !_negative.Contains(ep.Id))
            return new UiLoadResult(LoadOutcome.Offline, Array.Empty<Chapter>());

        Log.Information("chapters/resolve-ui ep={Title} url={Url}", ep.Title, ep.AudioUrl);
        var loaded = await EnsureLoadedAsync(ep);
        if (!loaded) return new UiLoadResult(LoadOutcome.NoChapters, Array.Empty<Chapter>());
        return new UiLoadResult(LoadOutcome.Loaded, ep.Chapters);
    }

    // Commands in this TUI operate on the selected episode by convention;
    // playing is only the fallback. Picking "playing first" surprises users
    // who select ATP in the sidebar, have a Darknet episode still playing in
    // the background, then run :chapter list and see the wrong feed's data.
    Episode? ResolveEpisode()
    {
        var sel = _ui.GetSelectedEpisode();
        if (sel != null) return sel;
        var playingId = _ui.GetNowPlayingId();
        if (playingId.HasValue) return _episodes.Find(playingId.Value);
        return null;
    }
}
