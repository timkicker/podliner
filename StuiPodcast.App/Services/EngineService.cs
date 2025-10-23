using Serilog;
using StuiPodcast.App.Debug;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Services;

// ==========================================================
// Engine service (create/switch/apply prefs)
// ==========================================================
sealed class EngineService
{
    readonly AppData _data;
    readonly MemoryLogSink _memLog;
    public SwappablePlayer? Current { get; private set; }
    string? _initialInfo;

    
    // in class EngineService
    public void ApplyPrefsToCurrent(SwappablePlayer sp)
    {
        try {
            var v = Math.Clamp(_data.Volume0_100, 0, 100);
            if (v != 0 || _data.Volume0_100 == 0) sp.SetVolume(v);
        } catch {}

        try {
            var s = _data.Speed; if (s <= 0) s = 1.0;
            sp.SetSpeed(Math.Clamp(s, 0.25, 3.0));
        } catch {}
    }

    
    public EngineService(AppData data, MemoryLogSink memLog)
    {
        _data = data;
        _memLog = memLog;
    }

    public SwappablePlayer Create(out string engineInfo)
    {
        var core = AudioPlayerFactory.Create(_data, out var info);
        engineInfo = info;
        _initialInfo = info;
        Current = new SwappablePlayer(core);
        return Current;
    }

    public void ApplyPrefsTo(IPlayer p)
    {
        try
        {
            if ((p.Capabilities & PlayerCapabilities.Volume) != 0)
            {
                var v = Math.Clamp(_data.Volume0_100, 0, 100);
                p.SetVolume(v);
            }
        } catch { }

        try
        {
            if ((p.Capabilities & PlayerCapabilities.Speed) != 0)
            {
                var s = _data.Speed;
                if (s <= 0) s = 1.0;
                p.SetSpeed(Math.Clamp(s, 0.25, 3.0));
            }
        } catch { }
    }

    public async Task SwitchAsync(SwappablePlayer player, string pref, Func<Task> onPersistTick)
    {
        try
        {
            _data.PreferredEngine = string.IsNullOrWhiteSpace(pref) ? "auto" : pref.Trim().ToLowerInvariant();
            _ = onPersistTick();

            var next = AudioPlayerFactory.Create(_data, out var info);
            Log.Information("engine created name={Engine} caps={Caps} info={Info}",
                player?.Name, (player?.Capabilities).ToString(), _initialInfo);
            ApplyPrefsTo(next);

            await player.SwapToAsync(next, old => { try { old.Stop(); } catch { } });
            Log.Information("engine switched current={Name} caps={Caps}", player.Name, player.Capabilities);
            // OSD is raised by callers (UI)
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "engine switch failed");
        }
    }
}

