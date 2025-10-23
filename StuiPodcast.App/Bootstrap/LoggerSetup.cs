using Serilog;
using StuiPodcast.App.Debug;

namespace StuiPodcast.App.Bootstrap;

// ==========================================================
// Logger setup
// ==========================================================
static class LoggerSetup
{
    public static void Configure(string? level, MemoryLogSink memLog)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        var min = Serilog.Events.LogEventLevel.Debug;
        switch ((level ?? "").Trim().ToLowerInvariant())
        {
            case "info":    min = Serilog.Events.LogEventLevel.Information; break;
            case "warn":
            case "warning": min = Serilog.Events.LogEventLevel.Warning; break;
            case "error":   min = Serilog.Events.LogEventLevel.Error; break;
            case "debug":
            default:        min = Serilog.Events.LogEventLevel.Debug; break;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(min)
            .Enrich.WithProperty("pid", Environment.ProcessId)
            .WriteTo.Sink(memLog)
            .WriteTo.File(
                Path.Combine(logDir, "podliner-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Exception}{NewLine}"
            )
            .CreateLogger();

        Log.Information("startup v={Version} rid={Rid} pid={Pid} cwd={Cwd}",
            typeof(Program).Assembly.GetName().Version,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            Environment.ProcessId,
            Environment.CurrentDirectory);
    }
}