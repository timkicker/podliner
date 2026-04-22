using System.Text.Json;
using System.Text.Json.Serialization;

namespace StuiPodcast.Infra.Storage;

// Common persistence scaffold for the JSON files we keep (appsettings,
// library, gpodder, downloads). Each concrete store defines what T it
// persists, where it lives, its default instance, and how to validate/
// normalize loaded data. Everything else — tolerant reads, atomic writes,
// debounced save, corrupt-file fallback, dispose-flushes-pending — lives here.
//
// Behavior preserved from the three hand-rolled stores this replaces:
//  - Read with comments + trailing commas tolerated (forgiving to hand edits).
//  - Write indented (human-readable; JSON files are small enough that size
//    doesn't matter and round-tripping edits stays pleasant).
//  - Atomic write: serialize to .tmp, flush, then File.Replace (or fallback
//    on platforms where Replace isn't supported).
//  - Debounced SaveAsync coalesces bursts from event handlers into one write.
//  - SaveNow bypasses the debouncer for shutdown/explicit flushes.
//  - ReadOnly lock-in if an UnauthorizedAccessException surfaces so we stop
//    trying to write into an inaccessible file.
public abstract class JsonStore<T> : IDisposable where T : class, new()
{
    #region paths + state
    public string FilePath { get; }
    public string TmpPath  { get; }

    public T Current { get; private set; } = new();

    public bool    IsReadOnly      => _readOnly;
    public string? ReadOnlyReason  => _readOnlyReason;

    public event Action? Changed;

    readonly TimeSpan _debounceInterval;
    readonly object _gate = new();
    Timer? _debounceTimer;
    volatile bool _savePending;
    volatile bool _isWriting;
    volatile bool _readOnly;
    volatile string? _readOnlyReason;
    #endregion

    #region json options
    protected static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    protected static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    #endregion

    #region ctor
    protected JsonStore(string filePath, TimeSpan debounceInterval)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath must be provided", nameof(filePath));

        FilePath = filePath;
        TmpPath  = filePath + ".tmp";
        _debounceInterval = debounceInterval;
    }
    #endregion

    #region extension points
    // Concrete stores return the instance used when the file is missing or
    // corrupt. Should return a fresh instance every call (not a shared one).
    protected virtual T CreateDefault() => new();

    // Called after each successful load to enforce invariants (clamp ranges,
    // fill in missing defaults, drop dangling references, etc.).
    protected virtual void ValidateAndNormalize(T instance) { }

    // Ensures the parent directory exists. Default creates
    // Path.GetDirectoryName(FilePath). Override for deeper prep if needed.
    protected virtual void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }
    #endregion

    #region load + save
    public T Load()
    {
        EnsureDirectory();

        // Remove an orphaned .tmp left behind by a crash during a previous write.
        try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch { /* best effort */ }

        T instance;
        if (!File.Exists(FilePath))
        {
            instance = CreateDefault();
        }
        else
        {
            try
            {
                using var fs = File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                instance = JsonSerializer.Deserialize<T>(fs, ReadOptions) ?? CreateDefault();
            }
            catch
            {
                // Corrupt JSON — start fresh. Keeps the app usable even if
                // the file got truncated or edited into nonsense.
                instance = CreateDefault();
            }
        }

        ValidateAndNormalize(instance);
        Current = instance;
        return instance;
    }

    // Debounced save. Multiple calls during the debounce window coalesce
    // into a single write when the timer fires.
    public void SaveAsync()
    {
        if (_readOnly) return;

        lock (_gate)
        {
            _savePending = true;
            _debounceTimer ??= new Timer(static s =>
            {
                var self = (JsonStore<T>)s!;
                self.TryPerformDebouncedSave();
            }, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    // Immediate atomic write, bypassing the debounce.
    public void SaveNow()
    {
        if (_readOnly) return;

        lock (_gate)
        {
            _savePending = false;
            _debounceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        try
        {
            WriteFileAtomic(Current);
            Changed?.Invoke();
        }
        catch (UnauthorizedAccessException ex)
        {
            MarkReadOnly(ex);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "JsonStore: SaveNow failed for {Path}", FilePath);
        }
    }
    #endregion

    #region debounce worker
    void TryPerformDebouncedSave()
    {
        if (_readOnly) return;

        lock (_gate)
        {
            if (_isWriting || !_savePending) return;
            _isWriting = true;
            _savePending = false;
        }

        try
        {
            WriteFileAtomic(Current);
            Changed?.Invoke();
        }
        catch (UnauthorizedAccessException ex)
        {
            MarkReadOnly(ex);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "JsonStore: debounced save failed for {Path}", FilePath);
        }
        finally
        {
            lock (_gate) _isWriting = false;
        }
    }
    #endregion

    #region file io
    void WriteFileAtomic(T value)
    {
        EnsureDirectory();

        using (var fs = File.Open(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            JsonSerializer.Serialize(writer, value, WriteOptions);
            writer.Flush();
            try { fs.Flush(true); } catch { /* flush-to-disk best effort */ }
        }

        if (File.Exists(FilePath))
        {
            try
            {
                File.Replace(TmpPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(FilePath);
                File.Move(TmpPath, FilePath);
            }
        }
        else
        {
            File.Move(TmpPath, FilePath);
        }

        try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch { /* best effort */ }
    }

    void MarkReadOnly(Exception ex)
    {
        _readOnly = true;
        _readOnlyReason = ex.GetType().Name + ": " + ex.Message;
    }
    #endregion

    #region dispose
    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _debounceTimer;
            _debounceTimer = null;
        }
        if (timer == null) return;

        // Stop the timer and wait briefly for any in-flight callback to finish
        // before flushing any pending save at shutdown.
        using var waitHandle = new ManualResetEvent(false);
        try { timer.Dispose(waitHandle); waitHandle.WaitOne(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }

        if (_savePending && !_readOnly) SaveNow();
    }
    #endregion
}
