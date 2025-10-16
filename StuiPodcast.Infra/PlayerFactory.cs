using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public static class PlayerFactory
{
    // Engine Auswahl: respektiert PreferredEngine; robustes Fallback + klare OSD-Infos
    public static IPlayer Create(AppData data, out string infoOsd)
    {
        var pref = (data.PreferredEngine ?? "auto").Trim().ToLowerInvariant();

        bool TryVlc(out IPlayer p, out string reason)
        {
            try
            {
                p = new LibVlcPlayer();
                reason = "ok";
                return true;
            }
            catch (Exception ex)
            {
                p = null!;
                reason = $"VLClib init failed: {Short(ex)}";
                return false;
            }
        }

        bool TryMpv(out IPlayer p, out string reason)
        {
            p = null!;
            reason = "";
            if (!ExecutableExists("mpv"))
            {
                reason = "mpv not found in PATH";
                return false;
            }
            try
            {
                p = new MpvIpcPlayer();
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

        bool TryFfp(out IPlayer p, out string reason)
        {
            if (!ExecutableExists("ffplay"))
            {
                p = null!;
                reason = "ffplay not found in PATH";
                return false;
            }
            p = new FfplayPlayer();
            reason = "ok";
            return true;
        }

        IPlayer? chosen = null;
        string why = "";

        // Preferred explicit choice first
        if (pref is "vlc" && !TryVlc(out chosen!, out why)) pref = "auto";
        if (pref is "mpv" && !TryMpv(out chosen!, out why)) pref = "auto";
        if (pref is "ffplay" && !TryFfp(out chosen!, out why)) pref = "auto";

        // Auto fallback chain: VLC → mpv → ffplay
        if (chosen == null)
        {
            if (TryVlc(out chosen!, out var w1))      why = $"VLC: {w1}";
            else if (TryMpv(out chosen!, out var w2)) why = $"mpv: {w2}";
            else if (TryFfp(out chosen!, out var w3)) why = $"ffplay: {w3}";
            else throw new InvalidOperationException("No audio engine available (libVLC/mpv/ffplay).");
            infoOsd = $"Engine: {chosen.Name} (fallback)";
        }
        else
        {
            infoOsd = $"Engine: {chosen.Name}";
        }

        data.LastEngineUsed = chosen.Name;
        Log.Debug("Chosen engine: {Engine} ({Why})", chosen.Name, why);
        return chosen;
    }

    // Cross-platform Executable lookup:
    //  - Unix: try 'which'
    //  - Windows: search PATH + PATHEXT, also allow direct Start to test
    private static bool ExecutableExists(string fileName)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Try to resolve via PATH + PATHEXT
                var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD")
                              .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                foreach (var dir in paths)
                {
                    foreach (var ext in pathext)
                    {
                        var candidate = Path.Combine(dir, fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext);
                        if (File.Exists(candidate)) return true;
                    }
                }
                // Last resort: try start & catch Win32Exception (file not found)
                using var p = new Process { StartInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true } };
                try { p.Start(); p.Kill(true); return true; }
                catch (Win32Exception) { return false; }
                catch { return false; }
            }
            else
            {
                // Unix-y: which
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
                if (!p.WaitForExit(1500)) { try { p.Kill(true); } catch { } }
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    private static string Short(Exception ex)
        => ex.GetType().Name + (string.IsNullOrWhiteSpace(ex.Message) ? "" : $": {ex.Message}");
}
