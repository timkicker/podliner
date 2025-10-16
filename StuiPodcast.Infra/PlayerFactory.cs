using System;
using System.Diagnostics;
using System.IO;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public static class PlayerFactory
{
    // StuiPodcast.Infra/PlayerFactory.cs
    public static IPlayer Create(StuiPodcast.Core.AppData data, out string infoOsd)
    {
        string pref = (data.PreferredEngine ?? "auto").Trim().ToLowerInvariant();

        bool TryVlc(out IPlayer p) { try { p = new LibVlcPlayer(); return true; } catch { p = null!; return false; } }
        bool TryMpv(out IPlayer p) { if (!ExeExists("mpv")) { p = null!; return false; } try { p = new MpvIpcPlayer(); return true; } catch { p = null!; return false; } }
        bool TryFfp(out IPlayer p) { if (!ExeExists("ffplay")) { p = null!; return false; } p = new FfplayPlayer(); return true; }

        IPlayer? chosen = null;
        if (pref == "vlc"   && !TryVlc(out chosen))  pref = "auto";
        if (pref == "mpv"   && !TryMpv(out chosen))  pref = "auto";
        if (pref == "ffplay"&& !TryFfp(out chosen))  pref = "auto";

        if (chosen == null) {
            // Fallback-Kette VLC → MPV → ffplay
            if (!TryVlc(out chosen) && !TryMpv(out chosen) && !TryFfp(out chosen))
                throw new InvalidOperationException("No audio engine available (libVLC/mpv/ffplay).");
            infoOsd = $"Engine: {chosen.Name} (fallback)";
        } else {
            infoOsd = $"Engine: {chosen.Name}";
        }

        data.LastEngineUsed = chosen.Name;
        return chosen;
    }


    private static bool ExeExists(string name)
    {
        try {
            var p = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "which",
                    Arguments = name,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            p.Start();
            p.WaitForExit(500);
            return p.ExitCode == 0;
        } catch { return false; }
    }
}