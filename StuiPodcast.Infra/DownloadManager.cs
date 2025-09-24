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
        readonly AppData _data;
        readonly HttpClient _http = new();
        readonly object _gate = new();
        CancellationTokenSource? _cts;
        Task? _worker;

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
            }
        }

        public void Stop()
        {
            lock (_gate) { _cts?.Cancel(); }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _worker?.Wait(250); } catch { }
            _http.Dispose();
        }

        public void Enqueue(Guid episodeId)
        {
            lock (_gate)
            {
                if (!_data.DownloadQueue.Contains(episodeId))
                    _data.DownloadQueue.Add(episodeId);
                SetState(episodeId, s => s.State = DownloadState.Queued);
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
            }
            EnsureRunning();
        }

        public void Cancel(Guid episodeId)
        {
            lock (_gate)
            {
                _data.DownloadQueue.Remove(episodeId);
                SetState(episodeId, s => s.State = DownloadState.Canceled);
            }
        }

        public DownloadState GetState(Guid episodeId)
        {
            if (_data.DownloadMap.TryGetValue(episodeId, out var st))
                return st.State;
            return DownloadState.None;
        }

        async Task WorkerLoopAsync(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                Guid nextId;
                lock (_gate)
                {
                    if (_data.DownloadQueue.Count == 0)
                    {
                        // nichts zu tun → kurz schlafen
                        Monitor.Wait(_gate, TimeSpan.FromSeconds(1));
                        continue;
                    }
                    nextId = _data.DownloadQueue[0];
                    _data.DownloadQueue.RemoveAt(0);
                }

                var ep = _data.Episodes.FirstOrDefault(e => e.Id == nextId);
                if (ep == null) continue;

                try
                {
                    await DownloadOneAsync(ep, cancel);
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
            }
        }

        async Task DownloadOneAsync(Episode ep, CancellationToken cancel)
        {
            var url = ep.AudioUrl;
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Episode has no AudioUrl");

            // Pfad
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
            SetState(ep.Id, s => { s.State = DownloadState.Running; s.LocalPath = dst; s.BytesReceived = 0; s.TotalBytes = null; });

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            SetState(ep.Id, s => s.TotalBytes = total);

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

            // Optional: Verifying (einfacher Schritt – Größe checken)
            SetState(ep.Id, s => s.State = DownloadState.Verifying);
            var fi = new FileInfo(tmp);
            if (total.HasValue && fi.Length != total.Value)
                throw new IOException("Size mismatch after download.");

            // Atomar finalisieren
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);

            // Done
            ep.Downloaded = true;
            SetState(ep.Id, s => { s.State = DownloadState.Done; s.LocalPath = dst; s.Error = null; });
        }

        string ResolveDownloadRoot()
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

        static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return string.Join(" ", cleaned.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        static string? GuessExtensionFromUrl(string url)
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
            } catch { }
            return null;
        }

        void SetState(Guid epId, Action<DownloadStatus> mutate)
        {
            var st = _data.DownloadMap.TryGetValue(epId, out var s) ? s : new DownloadStatus();
            mutate(st);
            st.UpdatedAt = DateTimeOffset.Now;
            _data.DownloadMap[epId] = st;
            try { StatusChanged?.Invoke(epId, st); } catch { }
        }
    }
}
