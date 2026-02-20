using System.Text.Json;
using System.Text.Json.Serialization;
using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

// Reads/writes gpodder.json; atomic write, no debounce (file is small).
public sealed class GpodderStore
{
    private readonly string _filePath;
    private readonly string _tmpPath;

    public GpodderSyncConfig Current { get; private set; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public GpodderStore(string configDir)
    {
        Directory.CreateDirectory(configDir);
        _filePath = Path.Combine(configDir, "gpodder.json");
        _tmpPath  = _filePath + ".tmp";
    }

    // Called once at startup; returns defaults if file missing.
    public GpodderSyncConfig Load()
    {
        // remove orphaned temp file from a previous crash
        try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { /* best effort */ }

        if (!File.Exists(_filePath))
        {
            Current = CreateDefault();
            return Current;
        }

        try
        {
            using var fs = File.Open(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cfg = JsonSerializer.Deserialize<GpodderSyncConfig>(fs, ReadOptions) ?? CreateDefault();
            if (string.IsNullOrWhiteSpace(cfg.DeviceId))
                cfg.DeviceId = DefaultDeviceId();
            Current = cfg;
            return Current;
        }
        catch
        {
            Current = CreateDefault();
            return Current;
        }
    }

    // Immediate atomic write.
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            using (var fs = File.Open(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
                JsonSerializer.Serialize(writer, Current, WriteOptions);
                writer.Flush();
                try { fs.Flush(true); } catch { /* flush to disk best effort */ }
            }

            if (File.Exists(_filePath))
            {
                try
                {
                    File.Replace(_tmpPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(_filePath);
                    File.Move(_tmpPath, _filePath);
                }
            }
            else
            {
                File.Move(_tmpPath, _filePath);
            }

            try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { /* best effort */ }
        }
        catch { /* leave logging to the caller */ }
    }

    private static GpodderSyncConfig CreateDefault() => new() { DeviceId = DefaultDeviceId() };

    private static string DefaultDeviceId()
    {
        var id = "podliner-" + Environment.MachineName.ToLowerInvariant();
        return id.Length > 64 ? id[..64] : id;
    }
}
