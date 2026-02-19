using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using Terminal.Gui;
using Tmds.DBus;

namespace StuiPodcast.App.Mpris;

sealed class MprisObject : IMprisMediaPlayer2, IMprisPlayer
{
    public static readonly ObjectPath Path = new("/org/mpris/MediaPlayer2");

    private readonly AppData _data;
    private readonly IAudioPlayer _player;
    private readonly PlaybackCoordinator _playback;

    public ObjectPath ObjectPath => Path;

    // Internal C# events — SignalWatcher bridges these to D-Bus signals
    public event Action<PropertyChanges>? Mp2PropertiesChanged;
    public event Action<PropertyChanges>? PlayerPropertiesChanged;
    public event Action<long>? SeekedSignal;

    public MprisObject(AppData data, IAudioPlayer player, PlaybackCoordinator playback)
    {
        _data = data;
        _player = player;
        _playback = playback;
    }

    // ── IMprisMediaPlayer2 ──────────────────────────────────────────────────

    Task IMprisMediaPlayer2.RaiseAsync() => Task.CompletedTask;
    Task IMprisMediaPlayer2.QuitAsync()  => Task.CompletedTask;

    Task<object> IMprisMediaPlayer2.GetAsync(string prop)
        => Task.FromResult(GetMp2Prop(prop));

    Task<IDictionary<string, object>> IMprisMediaPlayer2.GetAllAsync()
        => Task.FromResult(GetAllMp2Props());

    Task IMprisMediaPlayer2.SetAsync(string prop, object val) => Task.CompletedTask;

    Task<IDisposable> IMprisMediaPlayer2.WatchPropertiesAsync(Action<PropertyChanges> handler)
        => SignalWatcher.AddAsync(this, nameof(Mp2PropertiesChanged), handler);

    // ── IMprisPlayer ────────────────────────────────────────────────────────

    Task IMprisPlayer.NextAsync()
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (_playback.TryAdvanceToNext(out var next) && next != null)
                _playback.Play(next);
        });
        return Task.CompletedTask;
    }

    Task IMprisPlayer.PreviousAsync()
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (_player.State.Position > TimeSpan.FromSeconds(3))
                _player.SeekTo(TimeSpan.Zero);
            else if (_playback.TryFindPrev(out var prev) && prev != null)
                _playback.Play(prev);
        });
        return Task.CompletedTask;
    }

    Task IMprisPlayer.PauseAsync()
    {
        if (_player.State.IsPlaying) _player.TogglePause();
        return Task.CompletedTask;
    }

    Task IMprisPlayer.PlayPauseAsync()
    {
        _player.TogglePause();
        return Task.CompletedTask;
    }

    Task IMprisPlayer.StopAsync()
    {
        _player.Stop();
        return Task.CompletedTask;
    }

    Task IMprisPlayer.PlayAsync()
    {
        if (!_player.State.IsPlaying) _player.TogglePause();
        return Task.CompletedTask;
    }

    Task IMprisPlayer.SeekAsync(long offsetUs)
    {
        _player.SeekRelative(TimeSpan.FromMicroseconds(offsetUs));
        return Task.CompletedTask;
    }

    Task IMprisPlayer.SetPositionAsync(ObjectPath trackId, long posUs)
    {
        _player.SeekTo(TimeSpan.FromMicroseconds(posUs));
        return Task.CompletedTask;
    }

    Task IMprisPlayer.OpenUriAsync(string uri) => Task.CompletedTask;

    Task<IDisposable> IMprisPlayer.WatchSeekedAsync(Action<long> handler, Action<Exception> onError)
        => SignalWatcher.AddAsync(this, nameof(SeekedSignal), handler);

    Task<object> IMprisPlayer.GetAsync(string prop)
        => Task.FromResult(GetPlayerProp(prop));

    Task<IDictionary<string, object>> IMprisPlayer.GetAllAsync()
        => Task.FromResult(GetAllPlayerProps());

    Task IMprisPlayer.SetAsync(string prop, object val)
    {
        switch (prop)
        {
            case "Volume":
                if (val is double v) _player.SetVolume((int)(v * 100));
                break;
            case "Rate":
                if (val is double r) _player.SetSpeed(r);
                break;
        }
        return Task.CompletedTask;
    }

    Task<IDisposable> IMprisPlayer.WatchPropertiesAsync(Action<PropertyChanges> handler)
        => SignalWatcher.AddAsync(this, nameof(PlayerPropertiesChanged), handler);

    // ── Property helpers ────────────────────────────────────────────────────

    private object GetMp2Prop(string prop) => prop switch
    {
        "CanQuit"             => (object)true,
        "CanRaise"            => false,
        "HasTrackList"        => false,
        "Identity"            => "Podliner",
        "SupportedUriSchemes" => new[] { "http", "https" },
        "SupportedMimeTypes"  => new[] { "audio/mpeg", "audio/ogg", "audio/mp4", "audio/flac" },
        _ => throw new DBusException("org.freedesktop.DBus.Error.InvalidArgs", $"Unknown property '{prop}'")
    };

    private IDictionary<string, object> GetAllMp2Props() => new Dictionary<string, object>
    {
        ["CanQuit"]             = true,
        ["CanRaise"]            = false,
        ["HasTrackList"]        = false,
        ["Identity"]            = "Podliner",
        ["SupportedUriSchemes"] = new[] { "http", "https" },
        ["SupportedMimeTypes"]  = new[] { "audio/mpeg", "audio/ogg", "audio/mp4", "audio/flac" }
    };

    private object GetPlayerProp(string prop) => prop switch
    {
        "PlaybackStatus" => (object)GetPlaybackStatus(),
        "LoopStatus"     => "None",
        "Rate"           => _player.State.Speed,
        "Shuffle"        => false,
        "Metadata"       => GetMetadata(),
        "Volume"         => _player.State.Volume0_100 / 100.0,
        "Position"       => (long)_player.State.Position.TotalMicroseconds,
        "MinimumRate"    => 0.5,
        "MaximumRate"    => 3.0,
        "CanGoNext"      => true,
        "CanGoPrevious"  => true,
        "CanPlay"        => true,
        "CanPause"       => (object)_player.Capabilities.HasFlag(PlayerCapabilities.Pause),
        "CanSeek"        => _player.Capabilities.HasFlag(PlayerCapabilities.Seek),
        "CanControl"     => true,
        _ => throw new DBusException("org.freedesktop.DBus.Error.InvalidArgs", $"Unknown property '{prop}'")
    };

    private IDictionary<string, object> GetAllPlayerProps() => new Dictionary<string, object>
    {
        ["PlaybackStatus"] = GetPlaybackStatus(),
        ["LoopStatus"]     = "None",
        ["Rate"]           = _player.State.Speed,
        ["Shuffle"]        = false,
        ["Metadata"]       = GetMetadata(),
        ["Volume"]         = _player.State.Volume0_100 / 100.0,
        ["Position"]       = (long)_player.State.Position.TotalMicroseconds,
        ["MinimumRate"]    = 0.5,
        ["MaximumRate"]    = 3.0,
        ["CanGoNext"]      = true,
        ["CanGoPrevious"]  = true,
        ["CanPlay"]        = true,
        ["CanPause"]       = _player.Capabilities.HasFlag(PlayerCapabilities.Pause),
        ["CanSeek"]        = _player.Capabilities.HasFlag(PlayerCapabilities.Seek),
        ["CanControl"]     = true
    };

    private string GetPlaybackStatus()
    {
        var s = _player.State;
        if (s.IsPlaying)        return "Playing";
        if (s.EpisodeId.HasValue) return "Paused";
        return "Stopped";
    }

    public IDictionary<string, object> BuildMetadata() => GetMetadata();

    private IDictionary<string, object> GetMetadata()
    {
        var meta = new Dictionary<string, object>();
        var state = _player.State;

        if (state.EpisodeId.HasValue)
        {
            var ep = _data.Episodes.FirstOrDefault(e => e.Id == state.EpisodeId.Value);
            if (ep != null)
            {
                var feed = _data.Feeds.FirstOrDefault(f => f.Id == ep.FeedId);
                meta["mpris:trackid"] = new ObjectPath($"/org/podliner/track/{ep.Id:N}");
                meta["mpris:length"]  = ep.DurationMs * 1000L;
                meta["xesam:title"]   = ep.Title;
                meta["xesam:url"]     = ep.AudioUrl;
                if (feed != null)
                {
                    meta["xesam:album"]  = feed.Title;
                    meta["xesam:artist"] = new[] { feed.Title };  // must be string[], not string
                }
            }
        }

        if (!meta.ContainsKey("mpris:trackid"))
            meta["mpris:trackid"] = new ObjectPath("/org/mpris/MediaPlayer2/TrackList/NoTrack");

        return meta;
    }

    // ── Signal emission (called by MprisService) ────────────────────────────

    public void NotifyPlayerPropertiesChanged(IDictionary<string, object> changed)
    {
        if (changed.Count == 0) return;
        var pc = new PropertyChanges(changed.ToArray(), Array.Empty<string>());
        _ = Task.Run(() => PlayerPropertiesChanged?.Invoke(pc));
    }

    public void NotifySeeked(long positionUs)
        => _ = Task.Run(() => SeekedSignal?.Invoke(positionUs));
}
