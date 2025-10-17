using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra
{
    public sealed class DownloadManager : IDisposable
    {
        private readonly AppData _data;
        private readonly HttpClient _http = new();
        private readonly object _gate = new();

        private CancellationTokenSource? _cts;      // global worker CTS
        private Task? _worker;

        // pro-Job Abbruch (für laufende Downloads)
        private readonly Dictionary<Guid, CancellationTokenSource> _running = new();

        public event Action<Guid, DownloadStatus>? StatusChanged; // (episodeId, status)

        public DownloadManager(AppData data)
        {
            _data = data;
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
            // kein Wait/Join hier – lasst den Task enden
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
            // 1) Aus der Queue entfernen (falls dort)
            bool pulsed = false;
            lock (_gate)
            {
                if (_data.DownloadQueue.Remove(episodeId))
                {
                    SetState(episodeId, s => s.State = DownloadState.Canceled);
                    pulsed = true;
                }

                // 2) Falls gerade läuft → pro-Job-CTS canceln
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

        private async Task WorkerLoopAsync(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                Guid nextId;

                lock (_gate)
                {
                    while (_data.DownloadQueue.Count == 0 && !cancel.IsCancellationRequested)
                    {
                        // warten bis neue Arbeit kommt
                        Monitor.Wait(_gate, TimeSpan.FromSeconds(5));
                    }
                    if (cancel.IsCancellationRequested) break;

                    nextId = _data.DownloadQueue[0];
                    _data.DownloadQueue.RemoveAt(0);
                }

                var ep = _data.Episodes.FirstOrDefault(e => e.Id == nextId);
                if (ep == null)
                {
                    // unbekannte Episode → überspringen
                    SetState(nextId, s => { s.State = DownloadState.Failed; s.Error = "Episode not found."; });
                    continue;
                }

                // Schon vorhanden & markiert?
                if (_data.DownloadMap.TryGetValue(ep.Id, out var st0) &&
                    st0.State == DownloadState.Done &&
                    !string.IsNullOrWhiteSpace(st0.LocalPath) &&
                    File.Exists(st0.LocalPath))
                {
                    // Nichts tun – bereits fertig
                    continue;
                }

                // pro-Job CTS anlegen (linked mit globalem Cancel)
                CancellationTokenSource? jobCts = null;
                try
                {
                    jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    lock (_gate) { _running[ep.Id] = jobCts; }

                    await DownloadOneAsync(ep, jobCts.Token);
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
                    if (jobCts != null)
                    {
                        try { jobCts.Dispose(); } catch { }
                    }
                    lock (_gate) { _running.Remove(ep.Id); }
                }
            }
        }

        private async Task DownloadOneAsync(Episode ep, CancellationToken cancel)
        {
            var url = ep.AudioUrl;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Episode has no AudioUrl");

            // ---- Zielpfade vorbereiten (OS-sicher) ----
            var rootDir = ResolveDownloadRoot();
            var feed = _data.Feeds.FirstOrDefault(f => f.Id == ep.FeedId);
            var feedTitle = feed?.Title ?? "Podcast";

            // Versuch, eine passendere Extension zu finden:
            //  - Aus URL
            //  - Falls HEAD/GET später Content-Disposition liefert, aktualisieren wir ggf. (rare)
            var extFromUrl = PathSanitizer.GetExtension(url, ".mp3");

            var targetPath = PathSanitizer.BuildDownloadPath(
                baseDir: rootDir,
                feedTitle: feedTitle,
                episodeTitle: ep.Title ?? "episode",
                urlOrExtHint: extFromUrl
            );

            // Eindeutig machen (falls Datei bereits existiert)
            targetPath = PathSanitizer.EnsureUniquePath(targetPath);

            // Temp-Datei im gleichen Verzeichnis (atomarer Move)
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

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // pragmatischer UA hilft einzelnen Hosts (manche blocken „default“ Clients)
            req.Headers.UserAgent.ParseAdd("stui-podcast/1.0");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
            resp.EnsureSuccessStatusCode();

            // Gesamtgröße (falls bekannt)
            var total = resp.Content.Headers.ContentLength;
            SetState(ep.Id, s => s.TotalBytes = total);

            // Content-Disposition → ggf. Endung feinjustieren (selten, aber nice)
            try
            {
                if (resp.Content.Headers.ContentDisposition != null &&
                    !string.IsNullOrWhiteSpace(resp.Content.Headers.ContentDisposition.FileNameStar))
                {
                    var fn = resp.Content.Headers.ContentDisposition.FileNameStar!.Trim('"');
                    var ext = PathSanitizer.GetExtension(fn, extFromUrl);
                    if (!string.IsNullOrWhiteSpace(ext) && !targetPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = Path.GetDirectoryName(targetPath)!;
                        var baseName = Path.GetFileNameWithoutExtension(targetPath);
                        var newPath = Path.Combine(dir, baseName + ext);
                        // nur wenn frei
                        newPath = PathSanitizer.EnsureUniquePath(newPath);
                        // Temp-Name anpassen
                        tmpPath = newPath + ".part";
                        targetPath = newPath;

                        // State updaten, damit UI die finale Endung kennt
                        SetState(ep.Id, s => s.LocalPath = targetPath);
                    }
                }
            }
            catch { /* hint ist optional */ }

            try
            {
                await using (var net = await resp.Content.ReadAsStreamAsync(cancel))
                await using (var file = File.Create(tmpPath))
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    long got = 0;
                    while ((n = await net.ReadAsync(buf.AsMemory(0, buf.Length), cancel)) > 0)
                    {
                        await file.WriteAsync(buf.AsMemory(0, n), cancel);
                        got += n;

                        var cg = got; var ct = total;
                        SetState(ep.Id, s =>
                        {
                            s.BytesReceived = cg;
                            s.TotalBytes = ct;
                            s.State = DownloadState.Running;
                        });
                    }
                }

                // Verifying (einfach: Größencheck, wenn bekannt)
                SetState(ep.Id, s => s.State = DownloadState.Verifying);
                var fi = new FileInfo(tmpPath);
                if (total.HasValue && fi.Length != total.Value)
                    throw new IOException("Size mismatch after download.");

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
                // bei Cancel/Fehler: .part bereinigen
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
