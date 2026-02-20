using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using Tmds.DBus;

namespace StuiPodcast.App.Mpris;

sealed class MprisService : IAsyncDisposable
{
    private readonly AppData _data;
    private readonly IAudioPlayer _player;
    private readonly PlaybackCoordinator _playback;

    private MprisObject? _obj;
    private Connection? _conn;
    private PlaybackSnapshot _lastSnapshot;

    public MprisService(AppData data, IAudioPlayer player, PlaybackCoordinator playback)
    {
        _data = data;
        _player = player;
        _playback = playback;
    }

    public async Task StartAsync()
    {
        _obj = new MprisObject(_data, _player, _playback);
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

        var changed = new Dictionary<string, object>();

        var status = ToPlaybackStatus(snap);
        if (status != ToPlaybackStatus(prev))
            changed["PlaybackStatus"] = status;

        if (Math.Abs(snap.Speed - prev.Speed) > 0.001)
            changed["Rate"] = snap.Speed;

        if (snap.EpisodeId != prev.EpisodeId)
            changed["Metadata"] = _obj!.BuildMetadata();

        if (changed.Count > 0)
            _obj?.NotifyPlayerPropertiesChanged(changed);

        if (IsSeekDetected(prev, snap))
            _obj?.NotifySeeked((long)snap.Position.TotalMicroseconds);
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
