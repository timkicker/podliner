using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Player;

public static class AudioPlayerFactory
{
    // engine choice prefers vlc with os specific fallbacks
    public static IAudioPlayer Create(AppData data, out string infoOsd)
    {
        var pref = (data.PreferredEngine ?? "auto").Trim().ToLowerInvariant();

        bool TryVlc(out IAudioPlayer p, out string reason)
        {
            try
            {
                p = new LibVlcAudioPlayer();
                reason = "ok";
                return true;
            }
            catch (Exception ex)
            {
                p = null!;
                reason = $"libVLC init failed: {Short(ex)}";
                return false;
            }
        }

        bool TryMpv(out IAudioPlayer p, out string reason)
        {
            p = null!;
            if (!ExecutableExists("mpv"))
            {
                reason = "mpv not found in path";
                return false;
            }
            try
            {
                p = new MpvAudioPlayer();
                reason = "ok";
                return true;
            }
            catch (PlatformNotSupportedException pnse)
            {
                reason = $"platform: {pnse.Message}";
                return false;
            }
            catch (Exception ex)
            {
                reason = $"init error: {Short(ex)}";
                return false;
            }
        }

        bool TryFfp(out IAudioPlayer p, out string reason)
        {
            if (!ExecutableExists("ffplay"))
            {
                p = null!;
                reason = "ffplay not found in path";
                return false;
            }
            p = new FfplayAudioPlayer();
            reason = "ok";
            return true;
        }

        IAudioPlayer? chosen = null;
        string why = "";

        // explicit preference first
        if (pref is "vlc" && !TryVlc(out chosen!, out why)) pref = "auto";
        if (pref is "mpv" && !TryMpv(out chosen!, out why)) pref = "auto";
        if (pref is "ffplay" && !TryFfp(out chosen!, out why)) pref = "auto";

        // auto chain by os
        if (chosen == null)
        {
            bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            bool isLin = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            if (TryVlc(out chosen!, out var wVlc))
            {
                why = $"vlc: {wVlc}";
            }
            else
            {
                if (isWin || isMac)
                {
                    // windows or macos: try ffplay then mpv
                    if (TryFfp(out chosen!, out var wFfp))      { why = $"ffplay: {wFfp}"; }
                    else if (TryMpv(out chosen!, out var wMpv)) { why = $"mpv: {wMpv}"; }
                }
                else if (isLin)
                {
                    // linux: try mpv then ffplay
                    if (TryMpv(out chosen!, out var wMpv))      { why = $"mpv: {wMpv}"; }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; }
                }
                else
                {
                    // unknown os: try mpv then ffplay
                    if (TryMpv(out chosen!, out var wMpv))      { why = $"mpv: {wMpv}"; }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; }
                }

                if (chosen == null)
                    throw new InvalidOperationException("No audio engine available (libVLC/mpv/ffplay).");
            }

            // degraded hint for osd
            bool degraded =
                string.Equals(chosen.Name, "ffplay", StringComparison.OrdinalIgnoreCase) ||
                isWin && string.Equals(chosen.Name, "mpv", StringComparison.OrdinalIgnoreCase);

            infoOsd = degraded ? $"Engine: {chosen.Name} (fallback)" : $"Engine: {chosen.Name}";
        }
        else
        {
            infoOsd = $"Engine: {chosen.Name}";
        }

        // include network profile info in osd
        if (data.NetProfile == NetworkProfile.BadNetwork)
            infoOsd += " • Net: bad";

        data.LastEngineUsed = chosen.Name;
        Log.Debug("Chosen engine: {Engine} ({Why}) — NetProfile={Net}", chosen.Name, why, data.NetProfile);

        return chosen;
    }

    // cross platform executable lookup
    // unix uses which; windows scans path and pathext then last resort start attempt
    private static bool ExecutableExists(string fileName)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD")
                              .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
                            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var dir in paths)
                {
                    foreach (var ext in pathext)
                    {
                        var candidate = Path.Combine(dir,
                            fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext);
                        if (File.Exists(candidate)) return true;
                    }
                }

                // last resort: try to start and catch file not found
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                try { p.Start(); try { p.Kill(entireProcessTree: true); } catch { } return true; }
                catch (Win32Exception) { return false; }
                catch { return false; }
            }
            else
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        ArgumentList = { fileName },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    }
                };
                p.Start();
                if (!p.WaitForExit(1500)) { try { p.Kill(entireProcessTree: true); } catch { } }
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    private static string Short(Exception ex)
        => ex.GetType().Name + (string.IsNullOrWhiteSpace(ex.Message) ? "" : $": {ex.Message}");
}
