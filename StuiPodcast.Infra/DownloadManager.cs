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
            try { _worker?.Wait(250); } catch { }
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
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Episode has no AudioUrl");

            // Pfade vorbereiten
            var root = ResolveDownloadRoot();
            var feed = _data.Feeds.FirstOrDefault(f => f.Id == ep.FeedId);
            var feedDirName = SanitizeFileName(feed?.Title ?? "Podcast");
            var epiName = SanitizeFileName(ep.Title ?? "episode");
            var ext = GuessExtensionFromUrl(url) ?? ".mp3";
            var dir = Path.Combine(root, feedDirName);
            Directory.CreateDirectory(dir);
            var dst = Path.Combine(dir, $"{feedDirName} - {epiName}{ext}");

            // Temp-Datei für atomare Moves
            var tmp = dst + ".part";

            // Status: Running
            SetState(ep.Id, s => { s.State = DownloadState.Running; s.LocalPath = dst; s.BytesReceived = 0; s.TotalBytes = null; s.Error = null; });

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            SetState(ep.Id, s => s.TotalBytes = total);

            try
            {
                await using (var net = await resp.Content.ReadAsStreamAsync(cancel))
                await using (var file = File.Create(tmp))
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    long got = 0;
                    while ((n = await net.ReadAsync(buf.AsMemory(0, buf.Length), cancel)) > 0)
                    {
                        await file.WriteAsync(buf.AsMemory(0, n), cancel);
                        got += n;

                        var cg = got; var ct = total;
                        SetState(ep.Id, s => { s.BytesReceived = cg; s.TotalBytes = ct; s.State = DownloadState.Running; });
                    }
                }

                // Verifying (einfach: Größencheck, wenn bekannt)
                SetState(ep.Id, s => s.State = DownloadState.Verifying);
                var fi = new FileInfo(tmp);
                if (total.HasValue && fi.Length != total.Value)
                    throw new IOException("Size mismatch after download.");

                // Atomar finalisieren
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);

                ep.Downloaded = true;
                SetState(ep.Id, s => { s.State = DownloadState.Done; s.LocalPath = dst; s.Error = null; });
            }
            catch
            {
                // bei Cancel/Fehler: .part bereinigen
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
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

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return string.Join(" ", cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static string? GuessExtensionFromUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                var path = u.AbsolutePath.ToLowerInvariant();
                if (path.EndsWith(".mp3")) return ".mp3";
                if (path.EndsWith(".m4a")) return ".m4a";
                if (path.EndsWith(".aac")) return ".aac";
                if (path.EndsWith(".ogg")) return ".ogg";
                if (path.EndsWith(".opus")) return ".opus";
            }
            catch { }
            return null;
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
