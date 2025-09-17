using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.App.Debug;

sealed class CommandRouter
{
    readonly AppData _data;
    readonly FeedService _feeds;
    readonly IPlayer _player;
    readonly PlaybackCoordinator _playback;
    readonly Shell _ui;
    readonly Func<Task> _save;
    readonly Action _quit;

    public CommandRouter(
        AppData data,
        FeedService feeds,
        IPlayer player,
        PlaybackCoordinator playback,
        Shell ui,
        Func<Task> save,
        Action quit)
    {
        _data = data;
        _feeds = feeds;
        _player = player;
        _playback = playback;
        _ui = ui;
        _save = save;
        _quit = quit;
    }

    // entrypoint
    public async Task Handle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        var cmd = raw.Trim();
        try
        {
            // quit
            if (cmd is ":q" or ":quit") { _quit(); return; }

            if (cmd.StartsWith(":add "))
            {
                var url = cmd.Substring(5).Trim();
                if (string.IsNullOrWhiteSpace(url)) return;

                var f = await _feeds.AddFeedAsync(url);
                _ui.SetFeeds(_data.Feeds, f.Id);
                _ui.SetEpisodesForFeed(f.Id, _data.Episodes);
                await _save();
                return;
            }

            if (cmd == ":refresh")
            {
                var keep = _ui.GetSelectedFeedId();
                await _feeds.RefreshAllAsync();
                _ui.SetFeeds(_data.Feeds, keep ?? _data.Feeds.FirstOrDefault()?.Id);
                var fid = _ui.GetSelectedFeedId();
                if (fid is Guid g) _ui.SetEpisodesForFeed(g, _data.Episodes);
                await _save();
                return;
            }

            if (cmd == ":help" || cmd == ":h") { _ui.ShowKeysHelp(); return; }
            if (cmd == ":theme" || cmd == ":t") { _ui.ToggleTheme(); return; }
            if (cmd == ":logs") { _ui.ShowLogsOverlay(500); return; }
            if (cmd.StartsWith(":logs "))
            {
                var arg = cmd.Substring(6).Trim();
                if (int.TryParse(arg, out var n) && n > 0) _ui.ShowLogsOverlay(Math.Min(n, 5000));
                else _ui.ShowLogsOverlay(500);
                return;
            }

            if (cmd == ":play")
            {
                var ep = _ui.GetSelectedEpisode();
                if (ep == null) return;
                _playback.Play(ep);
                _ui.SetWindowTitle(ep.Title);
                return;
            }

            if (cmd == ":mark" || cmd == ":toggle-mark" || cmd == ":m")
            {
                var ep = _ui.GetSelectedEpisode();
                if (ep == null) return;
                ep.Played = !ep.Played;
                if (ep.Played && ep.LengthMs is long len) ep.LastPosMs = len;
                await _save();
                var fid = _ui.GetSelectedFeedId();
                if (fid is Guid g) _ui.SetEpisodesForFeed(g, _data.Episodes);
                _ui.ShowDetails(ep);
                return;
            }

            if (cmd.StartsWith(":search "))
            {
                var q = cmd.Substring(8).Trim();
                var fid = _ui.GetSelectedFeedId();
                if (fid is Guid g)
                {
                    var list = _data.Episodes.Where(e => e.FeedId == g);
                    if (!string.IsNullOrWhiteSpace(q))
                        list = list.Where(e =>
                            (e.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (e.DescriptionText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
                    _ui.SetEpisodesForFeed(g, list);
                }
                return;
            }


            // ----- player controls -----

            if (cmd == ":toggle")
            {
                _player.TogglePause();
                return;
            }

            if (cmd.StartsWith(":seek"))
            {
                var arg = cmd.Substring(5).Trim();
                if (string.IsNullOrWhiteSpace(arg)) return;

                // percent
                if (arg.EndsWith("%") && double.TryParse(arg.TrimEnd('%'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var pct))
                {
                    if (_player.State.Length is TimeSpan len)
                    {
                        var pos = TimeSpan.FromMilliseconds(len.TotalMilliseconds * Math.Clamp(pct/100.0, 0, 1));
                        _player.SeekTo(pos);
                    }
                    return;
                }

                // relative seconds "+10" / "-30"
                if ((arg.StartsWith("+") || arg.StartsWith("-")) && int.TryParse(arg, out var secsRel))
                {
                    _player.SeekRelative(TimeSpan.FromSeconds(secsRel));
                    return;
                }

                // mm:ss
                var parts = arg.Split(':');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var mm)
                    && int.TryParse(parts[1], out var ss))
                {
                    _player.SeekTo(TimeSpan.FromSeconds(mm*60 + ss));
                    return;
                }

                // plain seconds
                if (int.TryParse(arg, out var secsAbs))
                {
                    _player.SeekTo(TimeSpan.FromSeconds(secsAbs));
                    return;
                }

                return;
            }

            if (cmd.StartsWith(":vol"))
            {
                var arg = cmd.Substring(4).Trim();
                if (int.TryParse(arg, out var v))
                {
                    if (arg.StartsWith("+") || arg.StartsWith("-"))
                        _player.SetVolume(_player.State.Volume0_100 + v);
                    else
                        _player.SetVolume(v);
                }
                return;
            }

            if (cmd.StartsWith(":speed"))
            {
                var arg = cmd.Substring(6).Trim();
                if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp))
                {
                    if (arg.StartsWith("+") || arg.StartsWith("-"))
                        _player.SetSpeed(_player.State.Speed + sp);
                    else
                        _player.SetSpeed(sp);
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Command failed: {Cmd}", cmd);
            _ui.ShowError("Error", ex.Message);
        }
    }
}
