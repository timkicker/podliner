using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Player;

public static class AudioPlayerFactory
{
    #region main factory
    // engine selection with os-aware fallbacks
    public static IAudioPlayer Create(AppData data, out string infoOsd)
    {
        // normalize engine preference
        var rawPref = (data.PreferredEngine ?? "auto").Trim();
        var pref = rawPref.ToLowerInvariant() switch
        {
            "libvlc" => "vlc",
            "mf" => "mediafoundation",
            "" => "auto",
            var s => s
        };

        // detect os
        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isLin = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        Log.Information("[engine-detect] requested='{Raw}' normalized='{Norm}' os={{win:{Win}, mac:{Mac}, lin:{Lin}}}",
            rawPref, pref, isWin, isMac, isLin);

        IAudioPlayer? chosen = null;
        string why = "";

        // honor explicit preference; fallback to auto if failed
        if (pref is "vlc" && !TryVlc(out chosen!, out why)) pref = "auto";
        if (pref is "mpv" && !TryMpv(out chosen!, out why)) pref = "auto";
        if (pref is "ffplay" && !TryFfp(out chosen!, out why)) pref = "auto";
        if (pref is "mediafoundation" or "mf")
        {
            if (!TryMf(out chosen!, out why)) pref = "auto";
        }

        // try engines in os-preferred order until success
        if (chosen == null)
        {
            Log.Information("[engine-detect] auto chain start (os policy)");
            if (TryVlc(out chosen!, out var wVlc))
            {
                why = $"vlc: {wVlc}";
                Log.Information("[engine-detect] pick: vlc ({Why})", why);
            }
            else
            {
                if (isWin)
                {
                    if (TryMf(out chosen!, out var wMf)) { why = $"mediafoundation: {wMf}"; Log.Information("[engine-detect] pick: mediafoundation ({Why})", why); }
                    else if (TryMpv(out chosen!, out var wMpv)) { why = $"mpv: {wMpv}"; Log.Information("[engine-detect] pick: mpv ({Why})", why); }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; Log.Information("[engine-detect] pick: ffplay ({Why})", why); }
                }
                else if (isMac)
                {
                    if (TryMpv(out chosen!, out var wMpv)) { why = $"mpv: {wMpv}"; Log.Information("[engine-detect] pick: mpv ({Why})", why); }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; Log.Information("[engine-detect] pick: ffplay ({Why})", why); }
                }
                else if (isLin)
                {
                    if (TryMpv(out chosen!, out var wMpv)) { why = $"mpv: {wMpv}"; Log.Information("[engine-detect] pick: mpv ({Why})", why); }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; Log.Information("[engine-detect] pick: ffplay ({Why})", why); }
                }
                else
                {
                    if (TryMpv(out chosen!, out var wMpv)) { why = $"mpv: {wMpv}"; Log.Information("[engine-detect] pick: mpv ({Why})", why); }
                    else if (TryFfp(out chosen!, out var wFfp)) { why = $"ffplay: {wFfp}"; Log.Information("[engine-detect] pick: ffplay ({Why})", why); }
                }

                if (chosen == null)
                {
                    Log.Error("[engine-detect] no engine available (libVLC/mpv/ffplay) after auto chain");
                    throw new InvalidOperationException("No audio engine available (libVLC/mpv/ffplay).");
                }
            }
        }

        // set osd text and update state
        bool degraded = string.Equals(chosen.Name, "ffplay", StringComparison.OrdinalIgnoreCase);
        infoOsd = degraded ? $"Engine: {chosen.Name} (fallback)" : $"Engine: {chosen.Name}";
        if (data.NetProfile == NetworkProfile.BadNetwork) infoOsd += " • Net: bad";

        data.LastEngineUsed = chosen.Name;
        Log.Information("[engine-detect] chosen='{Eng}' reason='{Why}' net='{Net}'", chosen.Name, why, data.NetProfile);

        return chosen;
    }
    #endregion

    #region engine probing
    private static bool TryVlc(out IAudioPlayer p, out string reason)
    {
        Log.Information("[engine-detect] probe start: vlc");
        try
        {
            p = new LibVlcAudioPlayer();
            reason = "ok";
            Log.Information("[engine-detect] probe ok: vlc ({Reason})", reason);
            return true;
        }
        catch (Exception ex)
        {
            p = null!;
            reason = $"libVLC init failed: {Short(ex)}";
            Log.Warning("[engine-detect] probe fail: vlc ({Reason})", reason);
            return false;
        }
    }

    private static bool TryMf(out IAudioPlayer p, out string reason)
    {
        Log.Information("[engine-detect] probe start: mediafoundation");
        p = null!;
        if (!OperatingSystem.IsWindows())
        {
            reason = "not supported on this OS";
            Log.Warning("[engine-detect] probe fail: mediafoundation ({Reason})", reason);
            return false;
        }
        try
        {
            p = new MediaFoundationAudioPlayer();
            reason = "ok";
            Log.Information("[engine-detect] probe ok: mediafoundation");
            return true;
        }
        catch (Exception ex)
        {
            reason = $"init failed: {Short(ex)}";
            Log.Warning("[engine-detect] probe fail: mediafoundation ({Reason})", reason);
            return false;
        }
    }

    private static bool TryMpv(out IAudioPlayer p, out string reason)
    {
        Log.Information("[engine-detect] probe start: mpv");
        p = null!;
        if (!ExecutableExists("mpv"))
        {
            reason = "mpv not found in PATH";
            Log.Warning("[engine-detect] probe fail: mpv ({Reason})", reason);
            return false;
        }
        try
        {
            p = new MpvAudioPlayer();
            reason = "ok";
            Log.Information("[engine-detect] probe ok: mpv ({Reason})", reason);
            return true;
        }
        catch (PlatformNotSupportedException pnse)
        {
            reason = $"platform: {pnse.Message}";
            Log.Warning("[engine-detect] probe fail: mpv ({Reason})", reason);
            return false;
        }
        catch (Exception ex)
        {
            reason = $"init error: {Short(ex)}";
            Log.Warning("[engine-detect] probe fail: mpv ({Reason})", reason);
            return false;
        }
    }

    private static bool TryFfp(out IAudioPlayer p, out string reason)
    {
        Log.Information("[engine-detect] probe start: ffplay");
        if (!ExecutableExists("ffplay"))
        {
            p = null!;
            reason = "ffplay not found in PATH";
            Log.Warning("[engine-detect] probe fail: ffplay ({Reason})", reason);
            return false;
        }
        p = new FfplayAudioPlayer();
        reason = "ok";
        Log.Information("[engine-detect] probe ok: ffplay ({Reason})", reason);
        return true;
    }
    #endregion

    #region utilities
    // find executable in path using platform-specific approach
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
                        if (File.Exists(candidate))
                        {
                            Serilog.Log.Debug("[engine-detect] win PATH hit '{Candidate}'", candidate);
                            return true;
                        }
                    }
                }

                // fallback: try starting exe and catch file-not-found
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
                try
                {
                    p.Start();
                    try { p.Kill(entireProcessTree: true); } catch { }
                    Serilog.Log.Debug("[engine-detect] win start-probe success for '{Exe}'", fileName);
                    return true;
                }
                catch (Win32Exception)
                {
                    Serilog.Log.Debug("[engine-detect] win start-probe fail for '{Exe}'", fileName);
                    return false;
                }
                catch
                {
                    return false;
                }
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
                string so = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(1500)) { try { p.Kill(entireProcessTree: true); } catch { } }
                Serilog.Log.Debug("[engine-detect] which '{Exe}' exit={Exit} out='{Out}'", fileName, p.ExitCode, so.Trim());
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    private static string Short(Exception ex)
        => ex.GetType().Name + (string.IsNullOrWhiteSpace(ex.Message) ? "" : $": {ex.Message}");
    #endregion
}
