using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Download;

// Owns the downloads.json index — the small JSON document that tells us
// which episodes have been downloaded to disk across restarts. Separate
// from AppFacade/LibraryStore because the set of local files is tied to
// the filesystem, not the library contents.
//
// Responsibilities:
//   • Load: read the index file, filter out entries whose local file
//     no longer exists, push surviving entries back into
//     AppData.DownloadMap via the provided callback.
//   • Save: atomic write via temp + replace (matches the other stores
//     in Infra.Storage).
//   • Debounce: background timer coalesces rapid state transitions
//     (download done, cancelled, etc.) into a single disk write.
//
// DownloadManager still owns the gate/StatusChanged events; this class
// is a pure persistence sidecar.
internal sealed class DownloadIndexStore : IDisposable
{
    readonly string _indexPath;
    readonly string _tmpPath;
    readonly object _persistGate = new();
    Timer? _persistTimer;

    public DownloadIndexStore(string configDir)
    {
        _indexPath = Path.Combine(configDir, "downloads.json");
        _tmpPath   = _indexPath + ".tmp";
    }

    // Reads the on-disk index and invokes `onRestore` for every entry
    // whose file still exists. Entries missing their backing file are
    // silently skipped so a deleted episode doesn't revive as "Done".
    public int Load(Action<Guid, DownloadStatus> onRestore)
    {
        try
        {
            if (!File.Exists(_indexPath)) return 0;

            var json = File.ReadAllText(_indexPath);
            var idx = JsonSerializer.Deserialize<DownloadIndex>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            }) ?? new DownloadIndex();

            int restored = 0;
            foreach (var it in idx.Items ?? new List<DownloadIndex.Item>())
            {
                if (it.EpisodeId == Guid.Empty) continue;
                if (string.IsNullOrWhiteSpace(it.LocalPath)) continue;
                if (!File.Exists(it.LocalPath)) continue;

                onRestore(it.EpisodeId, new DownloadStatus
                {
                    State = DownloadState.Done,
                    LocalPath = it.LocalPath,
                    BytesReceived = 0,
                    TotalBytes = null,
                    UpdatedAt = DateTimeOffset.Now
                });
                restored++;
            }

            Log.Information("downloads: restored {Count} entries from index", restored);
            return restored;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "downloads: index load failed");
            return 0;
        }
    }

    // Arms the debounce timer. Multiple rapid calls collapse into a
    // single SaveNow after ~800ms of quiet — avoids re-serializing a
    // multi-MB JSON on every progress pulse during active downloads.
    public void SaveDebounced(Func<IReadOnlyList<DownloadIndex.Item>> collect)
    {
        lock (_persistGate)
        {
            _persistTimer ??= new Timer(_ =>
            {
                try { SaveNow(collect); } catch { }
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _persistTimer.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
        }
    }

    // Writes the index atomically via temp + replace so a crash mid-write
    // can't corrupt the existing file. Callers pass a snapshot function
    // because they own the lock discipline around DownloadMap; this class
    // only runs the I/O.
    public void SaveNow(Func<IReadOnlyList<DownloadIndex.Item>> collect)
    {
        var items = collect();
        var idx = new DownloadIndex { SchemaVersion = 1, Items = items.ToList() };

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);

        using (var fs = File.Open(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            JsonSerializer.Serialize(writer, idx, opts);
            writer.Flush();
            try { fs.Flush(true); } catch { }
        }

        if (File.Exists(_indexPath))
        {
            try { File.Replace(_tmpPath, _indexPath, null, ignoreMetadataErrors: true); }
            catch (PlatformNotSupportedException)
            {
                try { File.Delete(_indexPath); } catch { }
                File.Move(_tmpPath, _indexPath);
            }
        }
        else
        {
            File.Move(_tmpPath, _indexPath);
        }

        try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { }
    }

    public void Dispose()
    {
        lock (_persistGate)
        {
            try { _persistTimer?.Dispose(); } catch { }
            _persistTimer = null;
        }
    }
}
