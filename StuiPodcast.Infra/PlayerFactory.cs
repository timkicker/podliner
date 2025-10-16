using System;
using System.Diagnostics;
using System.IO;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra;

public static class PlayerFactory
{
    public static IPlayer Create(out string infoOsd)
    {
        // 1) libVLC
        try {
            var p = new LibVlcPlayer();
            infoOsd = $"Engine: {p.Name}";
            return p;
        } catch (Exception ex) {
            Log.Warning(ex, "LibVLC unavailable, trying mpv");
        }

        // 2) mpv via IPC
        if (ExeExists("mpv")) {
            try {
                var p = new MpvIpcPlayer();
                infoOsd = $"Engine: {p.Name}";
                return p;
            } catch (Exception ex) {
                Log.Warning(ex, "mpv IPC failed, trying ffplay");
            }
        }

        // 3) ffplay (letzte Stufe)
        if (ExeExists("ffplay")) {
            var p = new FfplayPlayer();
            infoOsd = $"Engine: {p.Name} (limited)";
            return p;
        }

        // 4) Fallback: harte Exception
        infoOsd = "No player engine found";
        throw new InvalidOperationException("No audio engine available (libVLC/mpv/ffplay not found).");
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