using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Download
{
    // on-disk index for finished downloads
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

    // download manager with queue, retries and atomic writes
    public sealed class DownloadManager : IDisposable
    {
        #region fields

        private readonly string _indexPath;
        private readonly string _indexTmpPath;

        private readonly object _persistGate = new();
        private Timer? _persistTimer;

        private readonly AppData _data;
        private readonly HttpClient _http;

        private readonly object _gate = new();

        private CancellationTokenSource? _cts;
        private Task? _worker;

        private readonly Dictionary<Guid, CancellationTokenSource> _running = new();

        public event Action<Guid, DownloadStatus>? StatusChanged;

        private const int CONNECT_TIMEOUT_MS = 4000;
        private const int REQUEST_HEADERS_TIMEOUT_MS = 15000; // header phase only
        private const int READ_TIMEOUT_MS    = 25000;         // per-read stall guard
        private const int MAX_RETRIES        = 3;
        private const int BACKOFF_BASE_MS    = 400;
        private const int PROGRESS_PULSE_MS  = 400;

        #endregion

        #region ctor and setup

        public DownloadManager(AppData data, string configDir)
        {
            _data = data;

            _indexPath    = Path.Combine(configDir, "downloads.json");
            _indexTmpPath = _indexPath + ".tmp";

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromMilliseconds(CONNECT_TIMEOUT_MS),
                AllowAutoRedirect = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 6,
                EnableMultipleHttp2Connections = true
            };

            _http = new HttpClient(handler, disposeHandler: true)
            {
                // no global transfer timeout; we handle headers + per-read
                Timeout = Timeout.InfiniteTimeSpan
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("podliner/1.0");

            TryLoadIndex();
        }

        #endregion

        #region index persistence

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

                    lock (_gate)
                    {
                        _data.DownloadMap[it.EpisodeId] = new DownloadStatus
                        {
                            State = DownloadState.Done,
                            LocalPath = it.LocalPath,
                            BytesReceived = 0,
                            TotalBytes = null,
                            UpdatedAt = DateTimeOffset.Now
                        };
                    }

                    try { StatusChanged?.Invoke(it.EpisodeId, _data.DownloadMap[it.EpisodeId]); } catch { }
                    restored++;
                }

                Log.Information("downloads: restored {Count} entries from index", restored);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "downloads: index load failed");
            }
        }

        void SaveIndexDebounced()
        {
            lock (_persistGate)
            {
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
                    try { File.Delete(_indexPath); } catch { }
                    File.Move(tmp, _indexPath);
                }
            }
            else
            {
                File.Move(tmp, _indexPath);
            }

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        #endregion

        #region public control api

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

            try
            {
                var t = _worker;
                if (t != null && !t.IsCompleted) t.Wait(2000);
            }
            catch { }

            try { _cts?.Dispose(); } catch { }

            lock (_gate)
            {
                foreach (var kv in _running.Values) { try { kv.Cancel(); kv.Dispose(); } catch { } }
                _running.Clear();
            }

            try { _persistTimer?.Dispose(); } catch { }
            _http.Dispose();
        }

        #endregion

        #region queue api

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
            lock (_gate)
            {
                if (_data.DownloadMap.TryGetValue(episodeId, out var st))
                    return st.State;
            }
            return DownloadState.None;
        }

        #endregion

        #region worker loop

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

                Episode? ep;
                lock (_gate)
                    ep = _data.Episodes.FirstOrDefault(e => e.Id == nextId);

                if (ep == null)
                {
                    SetState(nextId, s => { s.State = DownloadState.Failed; s.Error = "episode not found"; });
                    Log.Warning("dl/worker episode-not-found id={Id}", nextId);
                    continue;
                }

                bool alreadyDone;
                lock (_gate)
                {
                    alreadyDone = _data.DownloadMap.TryGetValue(ep.Id, out var st0) &&
                                  st0.State == DownloadState.Done &&
                                  !string.IsNullOrWhiteSpace(st0.LocalPath) &&
                                  File.Exists(st0.LocalPath);
                }
                if (alreadyDone)
                {
                    Log.Information("dl/worker already-done id={Id}", ep.Id);
                    continue;
                }

                CancellationTokenSource? jobCts = null;
                try
                {
                    jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    lock (_gate) { _running[ep.Id] = jobCts; }

                    await DownloadWithRetriesAsync(ep, jobCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("dl/worker canceled id={Id}", ep.Id);
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

        #endregion

        #region download flow

        private async Task DownloadWithRetriesAsync(Episode ep, CancellationToken cancel)
        {
            Exception? last = null;
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                Log.Debug("dl/attempt {Attempt}/{Max} id={Id} title={Title}",
                    attempt + 1, MAX_RETRIES + 1, ep.Id, ep.Title);

                try
                {
                    await DownloadOneAsync(ep, cancel).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    last = new IOException("operation timed out");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException ex) when (IsTransient(ex)) { last = ex; }
                catch (IOException ex)        when (IsTransient(ex))   { last = ex; }
                catch (SocketException ex)                              { last = ex; }
                catch (Exception ex)                                    { last = ex; break; }

                if (attempt < MAX_RETRIES)
                {
                    var delay = await ComputeRetryDelayAsync(last, attempt).ConfigureAwait(false);
                    Log.Debug("dl/retry wait {Delay}ms id={Id} cause={Cause}",
                        delay, ep.Id, last?.GetType().Name ?? "?");
                    try { await Task.Delay(delay, cancel).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
                    SetState(ep.Id, s => s.State = DownloadState.Running);
                }
            }

            Log.Error(last, "dl/failed-after-retries id={Id} title={Title}", ep.Id, ep.Title);
            throw new IOException($"download failed after retries: {last?.GetType().Name}: {last?.Message}", last);
        }

        private async Task DownloadOneAsync(Episode ep, CancellationToken outerCancel)
        {
            var url = ep.AudioUrl;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("episode has no audio url");

            var rootDir = ResolveDownloadRoot();

            string FeedTitle()
            {
                var feed = _data.Feeds.FirstOrDefault(f => f.Id == ep.FeedId);
                return feed?.Title ?? "podcast";
            }

            var feedTitle = FeedTitle();
            Log.Information("dl/start id={Id} title={Title} url=\"{Url}\" feed=\"{Feed}\"",
                ep.Id, ep.Title, url, feedTitle);

            var extFromUrl = DownloadPathSanitizer.GetExtension(url, ".mp3");

            var targetPathGuess = DownloadPathSanitizer.BuildDownloadPath(
                baseDir: rootDir,
                feedTitle: feedTitle,
                episodeTitle: ep.Title ?? "episode",
                urlOrExtHint: extFromUrl
            );
            targetPathGuess = DownloadPathSanitizer.EnsureUniquePath(targetPathGuess);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPathGuess)!);
            var tmpPath = targetPathGuess + ".part";

            bool resume = File.Exists(tmpPath);
            long resumeFrom = 0;
            if (resume)
            {
                try { resumeFrom = new FileInfo(tmpPath).Length; } catch { resumeFrom = 0; }
            }

            SetState(ep.Id, s =>
            {
                s.State = DownloadState.Running;
                s.LocalPath = targetPathGuess;
                s.BytesReceived = resume ? resumeFrom : 0;
                s.TotalBytes = null;
                s.Error = null;
            });

            using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(outerCancel);
            headersCts.CancelAfter(REQUEST_HEADERS_TIMEOUT_MS);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (resume && resumeFrom > 0)
            {
                req.Headers.Range = new RangeHeaderValue(resumeFrom, null);
                Log.Information("dl/resume request id={Id} from={From}", ep.Id, resumeFrom);
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);

            if ((int)resp.StatusCode == 429)
            {
                var retry = ParseRetryAfterMs(resp.Headers.RetryAfter);
                var ex = new HttpRequestException("429 too many requests");
                if (retry > 0) ex.Data["RetryAfterMs"] = retry;
                throw ex;
            }

            if ((int)resp.StatusCode == 408 || (int)resp.StatusCode >= 500)
            {
                throw new HttpRequestException($"server returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            if (resume && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Log.Information("dl/resume 416 → restart id={Id}, wiping .part", ep.Id);
                try { File.Delete(tmpPath); } catch { }
                resume = false;
                resumeFrom = 0;

                using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
                using var headersCts2 = CancellationTokenSource.CreateLinkedTokenSource(outerCancel);
                headersCts2.CancelAfter(REQUEST_HEADERS_TIMEOUT_MS);

                using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, headersCts2.Token).ConfigureAwait(false);
                resp2.EnsureSuccessStatusCode();

                targetPathGuess = await ReceiveToFileAsync(
                    resp2, ep, outerCancel, tmpPath, targetPathGuess,
                    allowHeaderFileNameAdjust: true, resumeFrom: 0
                ).ConfigureAwait(false);
                return;
            }

            resp.EnsureSuccessStatusCode();

            targetPathGuess = await ReceiveToFileAsync(
                resp, ep, outerCancel, tmpPath, targetPathGuess,
                allowHeaderFileNameAdjust: !resume,
                resumeFrom: resumeFrom
            ).ConfigureAwait(false);
        }

        // writes response content to disk and finalizes file name
        private async Task<string> ReceiveToFileAsync(
            HttpResponseMessage resp,
            Episode ep,
            CancellationToken outerCancel,
            string tmpPath,
            string targetPath,
            bool allowHeaderFileNameAdjust,
            long resumeFrom)
        {
            var total = resp.Content.Headers.ContentLength;
            bool identityEncoding = !(resp.Content.Headers.ContentEncoding?.Any() ?? false);

            string effectiveTargetPath = targetPath;
            string effectiveTmpPath = tmpPath;

            if (allowHeaderFileNameAdjust)
            {
                try
                {
                    var extHint = Path.GetExtension(effectiveTargetPath);
                    var cd = resp.Content.Headers.ContentDisposition;
                    var fn = cd?.FileNameStar ?? cd?.FileName;
                    if (!string.IsNullOrWhiteSpace(fn))
                    {
                        var ext = DownloadPathSanitizer.GetExtension(fn.Trim('"'), string.IsNullOrWhiteSpace(extHint) ? ".mp3" : extHint);
                        if (!string.IsNullOrWhiteSpace(ext) && !effectiveTargetPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        {
                            var dir = Path.GetDirectoryName(effectiveTargetPath)!;
                            var baseName = Path.GetFileNameWithoutExtension(effectiveTargetPath);
                            var newPath = Path.Combine(dir, baseName + ext);
                            newPath = DownloadPathSanitizer.EnsureUniquePath(newPath);

                            effectiveTargetPath = newPath;
                            effectiveTmpPath = newPath + ".part";
                            Log.Debug("dl/filename-from-header id={Id} → newTarget=\"{Target}\"", ep.Id, effectiveTargetPath);

                            SetState(ep.Id, s => s.LocalPath = effectiveTargetPath);
                        }
                    }
                }
                catch { }
            }

            SetState(ep.Id, s => s.TotalBytes = total.HasValue ? total + resumeFrom : null);

            long bytesEmitted = 0;
            var lastPulse = DateTime.UtcNow;

            try
            {
                await using var net = await resp.Content.ReadAsStreamAsync(outerCancel).ConfigureAwait(false);
                await using var file = new FileStream(effectiveTmpPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

                var buf = new byte[64 * 1024];
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(outerCancel);
                    readCts.CancelAfter(READ_TIMEOUT_MS);

                    int n;
                    try
                    {
                        n = await net.ReadAsync(buf.AsMemory(0, buf.Length), readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce) when (!outerCancel.IsCancellationRequested)
                    {
                        throw new IOException("read timeout", oce);
                    }

                    if (n <= 0) break;

                    await file.WriteAsync(buf.AsMemory(0, n), outerCancel).ConfigureAwait(false);
                    bytesEmitted += n;

                    var now = DateTime.UtcNow;
                    if ((now - lastPulse).TotalMilliseconds >= PROGRESS_PULSE_MS)
                    {
                        lastPulse = now;
                        var cg = resumeFrom + bytesEmitted;
                        var ct = total.HasValue ? resumeFrom + total.Value : (long?)null;

                        SetState(ep.Id, s =>
                        {
                            s.BytesReceived = cg;
                            s.TotalBytes = ct;
                            s.State = DownloadState.Running;
                        });
                    }
                }

                await file.FlushAsync(outerCancel).ConfigureAwait(false);
                sw.Stop();

                SetState(ep.Id, s => s.State = DownloadState.Verifying);
                var fi = new FileInfo(effectiveTmpPath);
                if (identityEncoding && total.HasValue)
                {
                    long expected = resumeFrom + total.Value;
                    long have = fi.Length;

                    if (have != expected)
                    {
                        const long TOLERANCE_BYTES = 64 * 1024;
                        if (Math.Abs(have - expected) > TOLERANCE_BYTES)
                        {
                            Log.Warning("dl/verify size-mismatch id={Id} have={Have} expected={Expected}", ep.Id, have, expected);
                            throw new IOException($"size mismatch after download (have {have}, expected {expected})");
                        }
                        else
                        {
                            Log.Warning("dl/verify tolerated-mismatch id={Id} have={Have} expected={Expected}", ep.Id, have, expected);
                        }
                    }
                    else
                    {
                        Log.Debug("dl/verify ok id={Id} size={Size}", ep.Id, have);
                    }
                }

                try
                {
                    var destDir = Path.GetDirectoryName(effectiveTargetPath)!;
                    Directory.CreateDirectory(destDir);

                    if (File.Exists(effectiveTargetPath))
                    {
                        try
                        {
                            var backup = Path.Combine(destDir, ".backup_" + Path.GetFileName(effectiveTargetPath));
                            File.Replace(effectiveTmpPath, effectiveTargetPath, backup, ignoreMetadataErrors: true);
                            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                        }
                        catch (PlatformNotSupportedException)
                        {
#if NET6_0_OR_GREATER
                            File.Move(effectiveTmpPath, effectiveTargetPath, overwrite: true);
#else
                            File.Delete(effectiveTargetPath);
                            File.Move(effectiveTmpPath, effectiveTargetPath);
#endif
                        }
                    }
                    else
                    {
#if NET6_0_OR_GREATER
                        File.Move(effectiveTmpPath, effectiveTargetPath, overwrite: false);
#else
                        File.Move(effectiveTmpPath, effectiveTargetPath);
#endif
                    }
                }
                catch
                {
                    try { if (File.Exists(effectiveTmpPath)) File.Delete(effectiveTmpPath); } catch { }
                    throw;
                }

                var mb = (resumeFrom + bytesEmitted) / (1024.0 * 1024.0);
                var mbps = mb / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                Log.Information("dl/done id={Id} wrote={MB:F1} MB dur={Sec:F2}s avg={MBps:F2} MB/s -> \"{Path}\"",
                    ep.Id, mb, sw.Elapsed.TotalSeconds, mbps, effectiveTargetPath);

                SetState(ep.Id, s =>
                {
                    s.State = DownloadState.Done;
                    s.LocalPath = effectiveTargetPath;
                    s.Error = null;
                    s.BytesReceived = resumeFrom + bytesEmitted;
                    s.TotalBytes = resumeFrom + (total ?? resumeFrom + bytesEmitted);
                });

                SaveIndexDebounced();
                return effectiveTargetPath;
            }
            catch
            {
                Log.Information("dl/tmp kept for resume id={Id} tmp=\"{Tmp}\" exists={Exists}", ep.Id, effectiveTmpPath, File.Exists(effectiveTmpPath));
                throw;
            }
        }

        #endregion

        #region helpers

        private static bool IsTransient(Exception ex)
        {
            if (ex is HttpRequestException) return true;
            if (ex is IOException ioex && ioex.InnerException is SocketException) return true;
            if (ex is IOException iox && iox.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return true;
            if (ex is SocketException) return true;
            return false;
        }

        private static int BackoffWithJitter(int attempt0)
        {
            var pow = 1 << attempt0;
            var baseMs = BACKOFF_BASE_MS * pow;
            var jitter = Random.Shared.Next(0, 150);
            return Math.Min(baseMs + jitter, 4000);
        }

        private async Task<int> ComputeRetryDelayAsync(Exception? last, int attempt)
        {
            if (last is HttpRequestException hre && hre.Data != null && hre.Data.Contains("RetryAfterMs"))
            {
                var v = hre.Data["RetryAfterMs"];
                if (v is int ms && ms > 0) return ms;
            }
            return BackoffWithJitter(attempt);
        }

        private int ParseRetryAfterMs(RetryConditionHeaderValue? retryAfter)
        {
            if (retryAfter == null) return 0;
            if (retryAfter.Delta.HasValue)
            {
                var ms = (int)Math.Clamp(retryAfter.Delta.Value.TotalMilliseconds, 0, 120_000);
                return ms;
            }
            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                var ms = (int)Math.Clamp(delta.TotalMilliseconds, 0, 120_000);
                return ms;
            }
            return 0;
        }

        private string ResolveDownloadRoot()
        {
            string root;
            lock (_gate)
            {
                root = _data.DownloadDir;
                if (string.IsNullOrWhiteSpace(root))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    root = Path.Combine(home, "Podcasts");
                    _data.DownloadDir = root;
                }
            }
            Directory.CreateDirectory(root);
            Log.Debug("dl/root resolved dir=\"{Dir}\"", root);
            return root;
        }

        private void SetState(Guid epId, Action<DownloadStatus> mutate)
        {
            DownloadStatus? before = null;
            DownloadStatus st;

            lock (_gate)
            {
                var had = _data.DownloadMap.TryGetValue(epId, out var s);
                if (had)
                {
                    before = new DownloadStatus
                    {
                        State = s.State, BytesReceived = s.BytesReceived, TotalBytes = s.TotalBytes,
                        LocalPath = s.LocalPath, Error = s.Error, UpdatedAt = s.UpdatedAt
                    };
                    st = s!;
                }
                else
                {
                    st = new DownloadStatus();
                }

                mutate(st);
                st.UpdatedAt = DateTimeOffset.Now;
                _data.DownloadMap[epId] = st;

                if (before == null || before.State != st.State)
                {
                    Log.Information("dl/state id={Id} {From}→{To} bytes={Recv}/{Total} path=\"{Path}\" err={Err}",
                        epId, before?.State ?? DownloadState.None, st.State,
                        st.BytesReceived, st.TotalBytes?.ToString() ?? "?", st.LocalPath, st.Error);
                }
                else if (st.State == DownloadState.Running)
                {
                    const long STEP = 25L * 1024 * 1024;
                    var crossed = before.BytesReceived / STEP != st.BytesReceived / STEP;
                    bool pctStep = false;
                    if (st.TotalBytes is long tot && tot > 0)
                    {
                        var oldPct = (int)(before.BytesReceived * 100 / tot);
                        var newPct = (int)(st.BytesReceived * 100 / tot);
                        pctStep = newPct / 10 != oldPct / 10;
                    }
                    if (crossed || pctStep)
                    {
                        Log.Debug("dl/progress id={Id} bytes={Recv}/{Total} (~{Pct}%)",
                            epId, st.BytesReceived, st.TotalBytes?.ToString() ?? "?",
                            st.TotalBytes is long t && t > 0 ? ((int)(st.BytesReceived * 100 / t)).ToString() : "?");
                    }
                }
            }

            try
            {
                if (_data.DownloadMap.TryGetValue(epId, out var stNow))
                    StatusChanged?.Invoke(epId, stNow);
            }
            catch { }
        }

        #endregion
    }
}
