using Serilog;
using Serilog.Events;
using StuiPodcast.App.Debug;
using System.Runtime.InteropServices;

namespace StuiPodcast.App.Bootstrap;

static class LoggerSetup
{
    public static void Configure(string? level, string? cliLogDir, bool noFileLogs, MemoryLogSink memLog)
    {
        var min = ParseLevel(level);
        var isJournal = IsJournal();
        var logDir = noFileLogs || isJournal ? null : ResolveLogDir(cliLogDir);

        var cfg = new LoggerConfiguration()
            .MinimumLevel.Is(min)
            .Enrich.WithProperty("pid", Environment.ProcessId)
            .WriteTo.Sink(memLog);


        if (!string.IsNullOrEmpty(logDir))
        {
            try
            {
                Directory.CreateDirectory(logDir!);
                if (IsWritable(logDir!))
                {
                    cfg = cfg.WriteTo.File(
                        Path.Combine(logDir!, "podliner-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        shared: true,
                        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Exception}{NewLine}"
                    );
                }
            }
            catch { }
        }

        Log.Logger = cfg.CreateLogger();

        Log.Information("startup v={Version} rid={Rid} pid={Pid} cwd={Cwd}",
            typeof(Program).Assembly.GetName().Version,
            RuntimeInformation.RuntimeIdentifier,
            Environment.ProcessId,
            Environment.CurrentDirectory);
    }
    
    #region helpers

    static LogEventLevel ParseLevel(string? level)
    {
        switch ((level ?? "").Trim().ToLowerInvariant())
        {
            case "info":    return LogEventLevel.Information;
            case "warn":
            case "warning": return LogEventLevel.Warning;
            case "error":   return LogEventLevel.Error;
            default:        return LogEventLevel.Debug;
        }
    }

    static bool IsJournal()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM"));
    }

    static string? ResolveLogDir(string? cliLogDir)
    {
        if (!string.IsNullOrWhiteSpace(cliLogDir))
            return cliLogDir;

        var overrideDir = Environment.GetEnvironmentVariable("PODLINER_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) return null;
            return Path.Combine(baseDir, "podliner", "logs");
        }

        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(state))
            return Path.Combine(state!, "podliner", "logs");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home!, ".local", "state", "podliner", "logs");

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(cache))
            return Path.Combine(cache!, "podliner", "logs");

        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home!, ".cache", "podliner", "logs");

        return null;
    }

    static bool IsWritable(string dir)
    {
        try
        {
            var test = Path.Combine(dir, ".write-test-" + Environment.ProcessId + ".tmp");
            File.WriteAllText(test, "");
            File.Delete(test);
            return true;
        }
        catch { return false; }
    }
    
    #endregion
}
