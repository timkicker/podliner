using StuiPodcast.App.Services;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using Tmds.DBus;

namespace StuiPodcast.App.Mpris;

sealed class MprisService : IAsyncDisposable
{
    private readonly AppData _data;
    private readonly IAudioPlayer _player;
    private readonly PlaybackCoordinator _playback;
    private readonly IEpisodeStore? _episodes;

    private MprisObject? _obj;
    private Connection? _conn;
    private PlaybackSnapshot _lastSnapshot;

    public MprisService(AppData data, IAudioPlayer player, PlaybackCoordinator playback, IEpisodeStore? episodes = null)
    {
        _data = data;
        _player = player;
        _playback = playback;
        _episodes = episodes;
    }

    public async Task StartAsync()
    {
        _obj = new MprisObject(_data, _player, _playback, _episodes);
        _conn = new Connection(Address.Session);
        await _conn.ConnectAsync();
        await _conn.RegisterObjectAsync(_obj);
        await _conn.RegisterServiceAsync("org.mpris.MediaPlayer2.podliner");
        _playback.SnapshotAvailable += OnSnapshot;
    }

    private void OnSnapshot(PlaybackSnapshot snap)
    {
        var prev = _lastSnapshot;
        _lastSnapshot = snap;

        // Snapshots fire ~4×/sec during playback; skip work when nothing changed.
        var status = ToPlaybackStatus(snap);
        bool statusChanged  = status != ToPlaybackStatus(prev);
        bool rateChanged    = Math.Abs(snap.Speed - prev.Speed) > 0.001;
        bool episodeChanged = snap.EpisodeId != prev.EpisodeId;
        bool seeked         = IsSeekDetected(prev, snap);

        if (!statusChanged && !rateChanged && !episodeChanged && !seeked) return;

        if (statusChanged || rateChanged || episodeChanged)
        {
            var changed = new Dictionary<string, object>();
            if (statusChanged)  changed["PlaybackStatus"] = status;
            if (rateChanged)    changed["Rate"] = snap.Speed;
            if (episodeChanged) changed["Metadata"] = _obj!.BuildMetadata();
            _obj?.NotifyPlayerPropertiesChanged(changed);
        }

        if (seeked) _obj?.NotifySeeked((long)snap.Position.TotalMicroseconds);
    }

    internal static string ToPlaybackStatus(PlaybackSnapshot snap)
    {
        if (snap.IsPlaying)          return "Playing";
        if (snap.EpisodeId.HasValue) return "Paused";
        return "Stopped";
    }

    internal static bool IsSeekDetected(PlaybackSnapshot prev, PlaybackSnapshot snap)
    {
        if (!prev.IsPlaying || snap.EpisodeId != prev.EpisodeId || !prev.EpisodeId.HasValue)
            return false;
        var elapsed = snap.Timestamp - prev.Timestamp;
        if (elapsed.TotalSeconds is not (> 0 and < 10))
            return false;
        var expected = prev.Position + TimeSpan.FromSeconds(elapsed.TotalSeconds * prev.Speed);
        return Math.Abs((snap.Position - expected).TotalSeconds) > 2.0;
    }

    public ValueTask DisposeAsync()
    {
        _playback.SnapshotAvailable -= OnSnapshot;
        try { _conn?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
