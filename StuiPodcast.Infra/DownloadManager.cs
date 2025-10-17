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
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra
{
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

        public DownloadManager(AppData data)
        {
            _data = data;

            // SocketsHttpHandler erlaubt uns, ConnectTimeout zu setzen
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromMilliseconds(CONNECT_TIMEOUT_MS),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 8
            };

            _http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMilliseconds(REQUEST_TIMEOUT_MS) // Obergrenze; wir ergänzen pro-Request-CTS für Feinschnitt
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("stui-podcast/1.0");
            _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
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
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                Monitor.PulseAll(_gate);
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

                var ep = _data.Episodes.FirstOrDefault(e => e.Id == nextId);
                if (ep == null)
                {
                    SetState(nextId, s => { s.State = DownloadState.Failed; s.Error = "Episode not found."; });
                    continue;
                }

                // Bereits vorhanden?
                if (_data.DownloadMap.TryGetValue(ep.Id, out var st0) &&
                    st0.State == DownloadState.Done &&
                    !string.IsNullOrWhiteSpace(st0.LocalPath) &&
                    File.Exists(st0.LocalPath))
                {
                    // erledigt – still weiter
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
                    SetState(ep.Id, s => s.State = DownloadState.Canceled);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Download failed for {title}", ep.Title);
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
                    Log.Debug("download retry {Attempt}/{Max} in {Delay}ms — {Title}", attempt + 1, MAX_RETRIES, delay, ep.Title);
                    try { await Task.Delay(delay, cancel); } catch (OperationCanceledException) { throw; }
                    // Status sichtbar halten
                    SetState(ep.Id, s => s.State = DownloadState.Running);
                }
            }

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
            req.Headers.UserAgent.ParseAdd("stui-podcast/1.0");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).ConfigureAwait(false);

            // Retry-Signale bei 408/429/5xx → Exception werfen, damit Retry-Hülle greift
            if ((int)resp.StatusCode == 408 || (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
            {
                throw new HttpRequestException($"Server returned { (int)resp.StatusCode } {resp.ReasonPhrase }");
            }
            resp.EnsureSuccessStatusCode();

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
                if (total.HasValue && fi.Length != total.Value)
                    throw new IOException($"Size mismatch after download (have {fi.Length}, expected {total.Value}).");

                // Atomar finalisieren
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tmpPath, targetPath);

                ep.Downloaded = true;
                SetState(ep.Id, s =>
                {
                    s.State = DownloadState.Done;
                    s.LocalPath = targetPath;
                    s.Error = null;
                });
            }
            catch
            {
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
            return root;
        }

        private void SetState(Guid epId, Action<DownloadStatus> mutate)
        {
            var st = _data.DownloadMap.TryGetValue(epId, out var s) ? s : new DownloadStatus();
            mutate(st);
            st.UpdatedAt = DateTimeOffset.Now;
            _data.DownloadMap[epId] = st;
            try { StatusChanged?.Invoke(epId, st); } catch { }
        }
    }
}
