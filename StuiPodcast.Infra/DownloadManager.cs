using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra
{
     class DownloadIndex
    {
        public int SchemaVersion { get; set; } = 1;
        public List<Item> Items { get; set; } = new();
        public sealed class Item
        {
            public Guid EpisodeId { get; set; }
            public string LocalPath { get; set; } = "";
        }
    }

    
    /// <summary>
    /// Robuster Download-Manager mit:
    /// - globalem Worker (Queue),
    /// - pro-Job-Cancel,
    /// - Timeouts (Connect/Overall/Read),
    /// - Retry mit Exponential Backoff + Jitter (5xx/408/429/transiente Netzwerkfehler),
    /// - atomarem Move nach .part-Datei,
    /// - moderatem Progress-Reporting.
    /// Öffentliche API & Events unverändert.
    /// </summary>
    
    
    public sealed class DownloadManager : IDisposable
    {
        private readonly string _indexPath;
        private readonly string _indexTmpPath;

        private readonly object _persistGate = new();
        private Timer? _persistTimer;
        private volatile bool _persistPending;

        private readonly AppData _data;

        // HttpClient mit vernünftigen Defaults; pro Request verwenden wir zusätzlich CTS für granularere Kontrolle
        private readonly HttpClient _http;

        private readonly object _gate = new();

        private CancellationTokenSource? _cts;      // global worker CTS
        private Task? _worker;

        // pro-Job Abbruch (für laufende Downloads)
        private readonly Dictionary<Guid, CancellationTokenSource> _running = new();

        public event Action<Guid, DownloadStatus>? StatusChanged; // (episodeId, status)

        // --- Tuning-Parameter (bei Bedarf zentral verstellen) ---
        private const int CONNECT_TIMEOUT_MS   = 4000;   // Verbindungsaufbau
        private const int REQUEST_TIMEOUT_MS   = 15000;  // gesamte HTTP-Anfrage, hart
        private const int READ_TIMEOUT_MS      = 12000;  // pro Read/Chunk
        private const int MAX_RETRIES          = 3;      // zusätzlich zum ersten Versuch → insgesamt max. 4 Versuche
        private const int BACKOFF_BASE_MS      = 400;    // 0,4s → 0,8s → 1,6s (+ Jitter)
        private const int PROGRESS_PULSE_MS    = 400;    // Progress-Events höchstens ~2.5x/s

        public DownloadManager(AppData data, string configDir)
        {
            _data = data;

            _indexPath   = Path.Combine(configDir, "downloads.json");
            _indexTmpPath = _indexPath + ".tmp";

            // SocketsHttpHandler ...
            var handler = new SocketsHttpHandler { /* wie gehabt */ };
            _http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMilliseconds(REQUEST_TIMEOUT_MS)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("podliner/1.0");
            _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

            // NEU: beim Start Index laden
            TryLoadIndex();
        }
        
        void TryLoadIndex()
{
    try
    {
        if (!File.Exists(_indexPath)) return;

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

            // In-Memory wiederherstellen
            _data.DownloadMap[it.EpisodeId] = new DownloadStatus
            {
                State = DownloadState.Done,
                LocalPath = it.LocalPath,
                BytesReceived = 0,
                TotalBytes = null,
                UpdatedAt = DateTimeOffset.Now
            };

            try { StatusChanged?.Invoke(it.EpisodeId, _data.DownloadMap[it.EpisodeId]); } catch { }
            restored++;
        }

        Serilog.Log.Information("downloads: restored {Count} entries from index", restored);
    }
    catch (Exception ex)
    {
        Serilog.Log.Debug(ex, "downloads: index load failed");
    }
}

void SaveIndexDebounced()
{
    lock (_persistGate)
    {
        _persistPending = true;
        _persistTimer ??= new Timer(_ =>
        {
            try { TrySaveIndex(); }
            catch { }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _persistTimer.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
    }
}

void TrySaveIndex()
{
    List<DownloadIndex.Item> items;
    lock (_gate)
    {
        items = _data.DownloadMap
            .Where(kv => kv.Value.State == DownloadState.Done &&
                         !string.IsNullOrWhiteSpace(kv.Value.LocalPath) &&
                         File.Exists(kv.Value.LocalPath))
            .Select(kv => new DownloadIndex.Item { EpisodeId = kv.Key, LocalPath = kv.Value.LocalPath! })
            .ToList();
    }

    var idx = new DownloadIndex { SchemaVersion = 1, Items = items };

    var opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
    var tmp = _indexTmpPath;

    // atomar schreiben
    using (var fs = File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, idx, opts);
        writer.Flush();
        try { fs.Flush(true); } catch { }
    }

    if (File.Exists(_indexPath))
    {
        try { File.Replace(tmp, _indexPath, null, ignoreMetadataErrors: true); }
        catch (PlatformNotSupportedException)
        {
            File.Delete(_indexPath);
            File.Move(tmp, _indexPath);
        }
    }
    else
    {
        File.Move(tmp, _indexPath);
    }

    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
}



        public void EnsureRunning()
        {
            lock (_gate)
            {
                if (_worker is { IsCompleted: false }) return;
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
                Monitor.PulseAll(_gate);
                Log.Debug("dl/ensure-running worker={Alive}", _worker is { IsCompleted: false });
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                Monitor.PulseAll(_gate);
                Log.Information("dl/stop requested");
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _cts?.Dispose(); } catch { }
            lock (_gate)
            {
                foreach (var kv in _running.Values) { try { kv.Cancel(); kv.Dispose(); } catch { } }
                _running.Clear();
            }
            _http.Dispose();
        }

        public void Enqueue(Guid episodeId)
        {
            lock (_gate)
            {
                if (!_data.DownloadQueue.Contains(episodeId))
                    _data.DownloadQueue.Add(episodeId);
                SetState(episodeId, s => s.State = DownloadState.Queued);
                Monitor.Pulse(_gate);
                Log.Information("dl/enqueue id={Id}", episodeId);
            }
            EnsureRunning();
        }

        public void ForceFront(Guid episodeId)
        {
            lock (_gate)
            {
                _data.DownloadQueue.Remove(episodeId);
                _data.DownloadQueue.Insert(0, episodeId);
                SetState(episodeId, s => s.State = DownloadState.Queued);
                Monitor.Pulse(_gate);
                Log.Information("dl/force-front id={Id}", episodeId);
            }
            EnsureRunning();
        }

        public void Cancel(Guid episodeId)
        {
            bool pulsed = false;
            lock (_gate)
            {
                if (_data.DownloadQueue.Remove(episodeId))
                {
                    SetState(episodeId, s => s.State = DownloadState.Canceled);
                    pulsed = true;
                }

                if (_running.TryGetValue(episodeId, out var jcts))
                {
                    try { jcts.Cancel(); } catch { }
                }
                if (pulsed) Monitor.Pulse(_gate);
                
                Log.Information("dl/cancel id={Id} removedFromQueue={Removed} wasRunning={WasRunning}",
                    episodeId, pulsed, _running.ContainsKey(episodeId));
            }
        }

        public DownloadState GetState(Guid episodeId)
        {
            if (_data.DownloadMap.TryGetValue(episodeId, out var st))
                return st.State;
            return DownloadState.None;
        }

        // --------------------------------------------------------------------
        // Worker-Loop
        // --------------------------------------------------------------------
        private async Task WorkerLoopAsync(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                Guid nextId;

                lock (_gate)
                {
                    while (_data.DownloadQueue.Count == 0 && !cancel.IsCancellationRequested)
                        Monitor.Wait(_gate, TimeSpan.FromSeconds(5));
                    if (cancel.IsCancellationRequested) break;

                    nextId = _data.DownloadQueue[0];
                    _data.DownloadQueue.RemoveAt(0);
                }
                
                
                Log.Debug("dl/worker pick id={Id} queueLeft={Queue}", nextId, _data.DownloadQueue.Count);

                var ep = _data.Episodes.FirstOrDefault(e => e.Id == nextId);
                if (ep == null)
                {
                    SetState(nextId, s => { s.State = DownloadState.Failed; s.Error = "Episode not found."; });
                    Log.Warning("dl/worker episode-not-found id={Id}", nextId);
                    continue;
                }

                // Bereits vorhanden?
                if (_data.DownloadMap.TryGetValue(ep.Id, out var st0) &&
                    st0.State == DownloadState.Done &&
                    !string.IsNullOrWhiteSpace(st0.LocalPath) &&
                    File.Exists(st0.LocalPath))
                {
                    // erledigt – still weiter
                    Log.Information("dl/worker already-done id={Id} file=\"{Path}\"", ep.Id, st0.LocalPath);
                    continue;
                }

                CancellationTokenSource? jobCts = null;
                try
                {
                    jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    lock (_gate) { _running[ep.Id] = jobCts; }

                    await DownloadWithRetriesAsync(ep, jobCts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("dl/worker canceled id={Id}", ep.Id);
                    SetState(ep.Id, s => s.State = DownloadState.Canceled);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "dl/worker failed id={Id} title={Title}", ep.Id, ep.Title);
                    SetState(ep.Id, s => { s.State = DownloadState.Failed; s.Error = ex.Message; });
                }
                finally
                {
                    if (jobCts != null) { try { jobCts.Dispose(); } catch { } }
                    lock (_gate) { _running.Remove(ep.Id); }
                }
            }
        }

        // --------------------------------------------------------------------
        // Retry-Hülle
        // --------------------------------------------------------------------
        private async Task DownloadWithRetriesAsync(Episode ep, CancellationToken cancel)
        {
            
            Exception? last = null;
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                Log.Debug("dl/attempt {Attempt}/{Max} id={Id} title={Title}",
                    attempt + 1, MAX_RETRIES + 1, ep.Id, ep.Title);

                try
                {
                    await DownloadOneAsync(ep, cancel);
                    return; // success
                }
                catch (OperationCanceledException)
                {
                    throw; // kein Retry auf Cancel
                }
                catch (HttpRequestException ex) when (IsTransient(ex))
                {
                    last = ex;
                }
                catch (IOException ex) when (IsTransient(ex))
                {
                    last = ex;
                }
                catch (SocketException ex)
                {
                    last = ex;
                }
                catch (Exception ex)
                {
                    // Nicht offensichtlich transient → nur einmal probieren (kein weiterer Retry)
                    last = ex;
                    break;
                }

                if (attempt < MAX_RETRIES)
                {
                    var delay = BackoffWithJitter(attempt);
                    Log.Debug("dl/retry wait {Delay}ms id={Id} title={Title} cause={Cause}",
                        delay, ep.Id, ep.Title, last?.GetType().Name ?? "?");
                    try { await Task.Delay(delay, cancel); } catch (OperationCanceledException) { throw; }
                    // Status sichtbar halten
                    SetState(ep.Id, s => s.State = DownloadState.Running);
                }
            }

            Log.Error(last, "dl/failed-after-retries id={Id} title={Title}", ep.Id, ep.Title);
            throw new IOException($"Download failed after retries: {last?.GetType().Name}: {last?.Message}", last);
        }

        private static bool IsTransient(Exception ex)
        {
            // Netzwerk / Zeitüberschreitungen / Abbrüche → retrybar
            if (ex is HttpRequestException hre) return true;
            if (ex is IOException ioex && ioex.InnerException is SocketException) return true;
            if (ex is IOException iox && iox.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return true;
            if (ex is SocketException) return true;
            return false;
        }

        private static int BackoffWithJitter(int attempt0)
        {
            var pow = 1 << attempt0; // 1,2,4,8
            var baseMs = BACKOFF_BASE_MS * pow;
            var jitter = Random.Shared.Next(0, 150);
            return Math.Min(baseMs + jitter, 4000);
        }

        // --------------------------------------------------------------------
        // Einzel-Download (ein Versuch)
        // --------------------------------------------------------------------
        private async Task DownloadOneAsync(Episode ep, CancellationToken outerCancel)
        {
            var url = ep.AudioUrl;
            
            
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Episode has no AudioUrl");

            // ---- Zielpfade vorbereiten ----
            var rootDir = ResolveDownloadRoot();
            var feed = _data.Feeds.FirstOrDefault(f => f.Id == ep.FeedId);
            var feedTitle = feed?.Title ?? "Podcast";
            
            Log.Information("dl/start id={Id} title={Title} url=\"{Url}\" feed=\"{Feed}\"",
                ep.Id, ep.Title, url, feedTitle);


            var extFromUrl = PathSanitizer.GetExtension(url, ".mp3");

            var targetPath = PathSanitizer.BuildDownloadPath(
                baseDir: rootDir,
                feedTitle: feedTitle,
                episodeTitle: ep.Title ?? "episode",
                urlOrExtHint: extFromUrl
            );
            targetPath = PathSanitizer.EnsureUniquePath(targetPath);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var tmpPath = targetPath + ".part";
            Log.Debug("dl/paths id={Id} tmp=\"{Tmp}\" target=\"{Target}\"", ep.Id, tmpPath, targetPath);


            // --- Status: Running (früh) ---
            SetState(ep.Id, s =>
            {
                s.State = DownloadState.Running;
                s.LocalPath = targetPath;
                s.BytesReceived = 0;
                s.TotalBytes = null;
                s.Error = null;
            });

            // Per-Request-CTS für präzisere Kontrolle (Gesamt + Read)
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(outerCancel);
            reqCts.CancelAfter(REQUEST_TIMEOUT_MS);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("podliner/1.0");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).ConfigureAwait(false);

            // Retry-Signale bei 408/429/5xx → Exception werfen, damit Retry-Hülle greift
            if ((int)resp.StatusCode == 408 || (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
            {
                throw new HttpRequestException($"Server returned { (int)resp.StatusCode } {resp.ReasonPhrase }");
            }
            resp.EnsureSuccessStatusCode();
            Log.Debug("dl/http ok id={Id} code={Code} len={Len} type={Type}",
                ep.Id, (int)resp.StatusCode,
                resp.Content.Headers.ContentLength?.ToString() ?? "?",
                resp.Content.Headers.ContentType?.MediaType ?? "?");


            var total = resp.Content.Headers.ContentLength;
            SetState(ep.Id, s => s.TotalBytes = total);

            // Content-Disposition → ggf. Endung feinjustieren
            try
            {
                var cd = resp.Content.Headers.ContentDisposition;
                var fn = cd?.FileNameStar ?? cd?.FileName;
                if (!string.IsNullOrWhiteSpace(fn))
                {
                    var ext = PathSanitizer.GetExtension(fn.Trim('"'), extFromUrl);
                    if (!string.IsNullOrWhiteSpace(ext) && !targetPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = Path.GetDirectoryName(targetPath)!;
                        var baseName = Path.GetFileNameWithoutExtension(targetPath);
                        var newPath = Path.Combine(dir, baseName + ext);
                        newPath = PathSanitizer.EnsureUniquePath(newPath);
                        tmpPath = newPath + ".part";
                        targetPath = newPath;
                        Log.Debug("dl/filename-from-header id={Id} headerName=\"{Name}\" → ext=\"{Ext}\" newTarget=\"{Target}\"",
                            ep.Id, fn, ext, targetPath);

                        SetState(ep.Id, s => s.LocalPath = targetPath);
                    }
                }
            }
            catch { /* optional */ }

            long bytesEmitted = 0;
            var lastPulse = DateTime.UtcNow;

            try
            {
                await using var net = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);
                await using var file = File.Create(tmpPath);

                var buf = new byte[64 * 1024];
                
                var sw = System.Diagnostics.Stopwatch.StartNew();

                
                while (true)
                {
                    // Read mit separatem Read-Timeout absichern
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
                    readCts.CancelAfter(READ_TIMEOUT_MS);
                    var n = await net.ReadAsync(buf.AsMemory(0, buf.Length), readCts.Token).ConfigureAwait(false);
                    if (n <= 0) break;

                    await file.WriteAsync(buf.AsMemory(0, n), reqCts.Token).ConfigureAwait(false);
                    bytesEmitted += n;

                    var now = DateTime.UtcNow;
                    if ((now - lastPulse).TotalMilliseconds >= PROGRESS_PULSE_MS)
                    {
                        lastPulse = now;
                        var cg = bytesEmitted; var ct = total;
                        SetState(ep.Id, s =>
                        {
                            s.BytesReceived = cg;
                            s.TotalBytes = ct;
                            s.State = DownloadState.Running;
                        });
                    }
                }

                // Verifying
                SetState(ep.Id, s => s.State = DownloadState.Verifying);

                var fi = new FileInfo(tmpPath);
                Log.Warning("dl/verify-size-mismatch id={Id} have={Have} expected={Expected}", ep.Id, fi.Length, total.Value);
                if (total.HasValue && fi.Length != total.Value)
                    throw new IOException($"Size mismatch after download (have {fi.Length}, expected {total.Value}).");

                // Atomar finalisieren
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tmpPath, targetPath);

                
                sw.Stop();
                var mb = bytesEmitted / (1024.0 * 1024.0);
                var mbps = mb / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                Log.Information("dl/done id={Id} wrote={MB:F1} MB dur={Sec:F2}s avg={MBps:F2} MB/s -> \"{Path}\"",
                    ep.Id, mb, sw.Elapsed.TotalSeconds, mbps, targetPath);

                
                StatusChanged?.Invoke(ep.Id, new DownloadStatus {
                    State = DownloadState.Done,
                });
                SetState(ep.Id, s =>
                {
                    s.State = DownloadState.Done;
                    s.LocalPath = targetPath;
                    s.Error = null;
                });
                SaveIndexDebounced();

            }
            catch
            {
                Log.Warning("dl/cleanup tmp-delete id={Id} tmp=\"{Tmp}\" exists={Exists}", ep.Id, tmpPath, File.Exists(tmpPath));
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        private string ResolveDownloadRoot()
        {
            var root = _data.DownloadDir;
            if (string.IsNullOrWhiteSpace(root))
            {
                // default: ~/Podcasts
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = Path.Combine(home, "Podcasts");
                _data.DownloadDir = root;
            }
            Directory.CreateDirectory(root);
            Log.Debug("dl/root resolved dir=\"{Dir}\"", root);

            return root;
        }

        private void SetState(Guid epId, Action<DownloadStatus> mutate)
        {
            var had = _data.DownloadMap.TryGetValue(epId, out var s);
            var before = had ? new DownloadStatus {
                State = s.State, BytesReceived = s.BytesReceived, TotalBytes = s.TotalBytes,
                LocalPath = s.LocalPath, Error = s.Error, UpdatedAt = s.UpdatedAt
            } : null;

            var st = had ? s! : new DownloadStatus();
            mutate(st);
            st.UpdatedAt = DateTimeOffset.Now;
            _data.DownloadMap[epId] = st;

            try { StatusChanged?.Invoke(epId, st); } catch { }

            // Logging: nur bei State-Änderung oder signifikantem Fortschritt
            if (!had || before!.State != st.State)
            {
                Log.Information("dl/state id={Id} {From}→{To} bytes={Recv}/{Total} path=\"{Path}\" err={Err}",
                    epId, had ? before!.State : DownloadState.None, st.State,
                    st.BytesReceived, st.TotalBytes?.ToString() ?? "?", st.LocalPath, st.Error);
            }
            else if (st.State == DownloadState.Running)
            {
                // alle ~25 MB (oder wenn Total bekannt: alle ~10%)
                const long STEP = 25L * 1024 * 1024;
                var crossed = (before!.BytesReceived / STEP) != (st.BytesReceived / STEP);
                bool pctStep = false;
                if (st.TotalBytes is long tot && tot > 0)
                {
                    var oldPct = (int)(before.BytesReceived * 100 / tot);
                    var newPct = (int)(st.BytesReceived * 100 / tot);
                    pctStep = (newPct / 10) != (oldPct / 10); // 0,10,20,...
                }
                if (crossed || pctStep)
                {
                    Log.Debug("dl/progress id={Id} bytes={Recv}/{Total} (~{Pct}%)",
                        epId, st.BytesReceived, st.TotalBytes?.ToString() ?? "?",
                        (st.TotalBytes is long t && t > 0) ? ((int)(st.BytesReceived * 100 / t)).ToString() : "?");
                }
            }
        }


    }
}
