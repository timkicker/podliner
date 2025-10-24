using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Services;

sealed class EngineService
{
    readonly AppData _data;
    readonly MemoryLogSink _memLog;
    public SwappableAudioPlayer? Current { get; private set; }
    string? _initialInfo;

    // apply stored prefs to a swappable player
    public void ApplyPrefsToCurrent(SwappableAudioPlayer sp)
    {
        try
        {
            var v = Math.Clamp(_data.Volume0_100, 0, 100);
            if (v != 0 || _data.Volume0_100 == 0) sp.SetVolume(v);
        }
        catch { }

        try
        {
            var s = _data.Speed; if (s <= 0) s = 1.0;
            sp.SetSpeed(Math.Clamp(s, 0.25, 3.0));
        }
        catch { }
    }

    public EngineService(AppData data, MemoryLogSink memLog)
    {
        _data = data;
        _memLog = memLog;
    }

    public SwappableAudioPlayer Create(out string engineInfo)
    {
        var core = AudioPlayerFactory.Create(_data, out var info);
        engineInfo = info;
        _initialInfo = info;
        Current = new SwappableAudioPlayer(core);
        return Current;
    }

    // apply stored prefs to a raw player
    public void ApplyPrefsTo(IAudioPlayer p)
    {
        try
        {
            if ((p.Capabilities & PlayerCapabilities.Volume) != 0)
            {
                var v = Math.Clamp(_data.Volume0_100, 0, 100);
                p.SetVolume(v);
            }
        }
        catch { }

        try
        {
            if ((p.Capabilities & PlayerCapabilities.Speed) != 0)
            {
                var s = _data.Speed;
                if (s <= 0) s = 1.0;
                p.SetSpeed(Math.Clamp(s, 0.25, 3.0));
            }
        }
        catch { }
    }

    public async Task SwitchAsync(SwappableAudioPlayer audioPlayer, string pref, Func<Task> onPersistTick)
    {
        try
        {
            _data.PreferredEngine = string.IsNullOrWhiteSpace(pref) ? "auto" : pref.Trim().ToLowerInvariant();
            _ = onPersistTick();

            var next = AudioPlayerFactory.Create(_data, out var info);
            Log.Information("engine created name={Engine} caps={Caps} info={Info}",
                next?.Name, next?.Capabilities, info);

            ApplyPrefsTo(next);

            await audioPlayer.SwapToAsync(next, old => { try { old.Stop(); } catch { } });
            Log.Information("engine switched current={Name} caps={Caps}", audioPlayer.Name, audioPlayer.Capabilities);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "engine switch failed");
        }
    }
}
