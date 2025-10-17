// PlayerFactory.cs
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
    // Engine-Auswahl: bevorzugt VLC; OS-spezifische Fallback-Ketten
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
                reason = $"libVLC init failed: {Short(ex)}";
                return false;
            }
        }

        bool TryMpv(out IPlayer p, out string reason)
        {
            p = null!;
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

        // 1) Explizite Präferenz zuerst
        if (pref is "vlc" && !TryVlc(out chosen!, out why)) pref = "auto";
        if (pref is "mpv" && !TryMpv(out chosen!, out why)) pref = "auto";
        if (pref is "ffplay" && !TryFfp(out chosen!, out why)) pref = "auto";

        // 2) Auto-Kette nach OS
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
                    // Windows/macOS: ffplay → mpv
                    if (TryFfp(out chosen!, out var wFfp))      { why = $"ffplay: {wFfp}"; }
                    else if (TryMpv(out chosen!, out var wMpv)) { why = $"mpv: {wMpv}"; }
                }
                else if (isLin)
                {
                    // Linux: mpv → ffplay
                    if (TryMpv(out chosen!, out var wMpv))      { why = $"mpv: {wMpv}"; }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; }
                }
                else
                {
                    // Unbekanntes OS: mpv → ffplay
                    if (TryMpv(out chosen!, out var wMpv))      { why = $"mpv: {wMpv}"; }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; }
                }

                if (chosen == null)
                    throw new InvalidOperationException("No audio engine available (libVLC/mpv/ffplay).");
            }

            // Degradierter Hinweis (für OSD)
            bool degraded =
                string.Equals(chosen.Name, "ffplay", StringComparison.OrdinalIgnoreCase) ||
                (isWin && string.Equals(chosen.Name, "mpv", StringComparison.OrdinalIgnoreCase)); // optional

            infoOsd = degraded ? $"Engine: {chosen.Name} (fallback)" : $"Engine: {chosen.Name}";
        }
        else
        {
            infoOsd = $"Engine: {chosen.Name}";
        }

        // Netzprofil in OSD mit anzeigen (reine Info; Engines können es separat berücksichtigen)
        if (data.NetProfile == NetworkProfile.BadNetwork)
            infoOsd += " • Net: bad";

        data.LastEngineUsed = chosen.Name;
        Log.Debug("Chosen engine: {Engine} ({Why}) — NetProfile={Net}", chosen.Name, why, data.NetProfile);

        return chosen;
    }

    // Cross-platform Executable lookup:
    //  - Unix: 'which'
    //  - Windows: PATH + PATHEXT + letzter Startversuch (abgefangen)
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

                // Letzter Versuch: Starten & Fehler fangen (Datei nicht gefunden)
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
