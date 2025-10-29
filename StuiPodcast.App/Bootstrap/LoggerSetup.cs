using Serilog;
using Serilog.Events;
using StuiPodcast.App.Debug;

namespace StuiPodcast.App.Bootstrap;

static class LoggerSetup
{
    public static void Configure(string? level, MemoryLogSink memLog)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);

        LogEventLevel min;
        switch ((level ?? "").Trim().ToLowerInvariant())
        {
            case "info":    min = LogEventLevel.Information; break;
            case "warn":
            case "warning": min = LogEventLevel.Warning; break;
            case "error":   min = LogEventLevel.Error; break;
            default:        min = LogEventLevel.Debug; break;
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