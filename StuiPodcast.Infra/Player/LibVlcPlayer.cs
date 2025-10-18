using Serilog;
using StuiPodcast.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using VLC = LibVLCSharp.Shared;

namespace StuiPodcast.Infra.Player
{
    /// <summary>
    /// LibVLC-basierter Player, der LibVLC **aus NuGet-Runtime-Assets** lädt.
    /// Kein Pfad-Resolver, kein externes VLC nötig.
    /// </summary>
    public sealed class LibVlcPlayer : IPlayer
    {
        private readonly VLC.LibVLC _lib;
        private readonly VLC.MediaPlayer _mp;
        private VLC.Media? _media;
        private readonly object _sync = new();

        private long? _pendingSeekMs;
        private int _sessionId;
        private bool _ready; // Erst „spielend“, wenn echte Länge/Time gesichtet

        public string Name => "vlc";

        public PlayerCapabilities Capabilities =>
            PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
            PlayerCapabilities.Seek | PlayerCapabilities.Volume | PlayerCapabilities.Speed |
            PlayerCapabilities.Network | PlayerCapabilities.Local;

        public PlayerState State { get; } = new();
        public event Action<PlayerState>? StateChanged;

        public LibVlcPlayer()
        {
            // Lädt libvlc aus NuGet (z. B. VideoLAN.LibVLC.Windows)
            VLC.Core.Initialize();

            // Optionale Log-Datei (hilfreich bei Supportfällen)
            var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "podliner-vlc.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }

            // Runtime-Optionen: leise + robuste Netzwerk-Buffer
            var opts = new[]
            {
                "--no-video",
                "--no-video-title-show",
                "--quiet","--verbose=0","--no-color",
                "--no-xlib",
                "--input-fast-seek",
                "--file-caching=1000",
                "--network-caching=2000",
                "--tcp-caching=1200",
                "--http-reconnect",
                "--file-logging", $"--logfile={logPath}"
            };

            _lib = new VLC.LibVLC(opts);
            _mp = new VLC.MediaPlayer(_lib);

            // Events verdrahten
            _mp.Playing += OnPlaying;
            _mp.TimeChanged += OnTimeChanged;
            _mp.LengthChanged += OnLengthChanged;
            _mp.EndReached += OnEndReached;
            _mp.EncounteredError += OnEncounteredError;
            _mp.Stopped += (_, __) => { lock (_sync) { State.IsPlaying = false; SafeFire(); } };

            // Startzustand
            try { _mp.Volume = Math.Clamp(State.Volume0_100, 0, 100); } catch { }
            try { _mp.SetRate((float)Math.Clamp(State.Speed, 0.25, 3.0)); } catch { }
            State.Capabilities = Capabilities;

            Log.Information("LibVLC initialized (nuget runtimes)");
        }

        public void Play(string url, long? startMs = null)
        {
            lock (_sync)
            {
                _sessionId++;
                var sid = _sessionId;
                Log.Information("[#{sid}] Play {url} (startMs={startMs})", sid, url, startMs);

                _ready = false;

                try { if (_mp.IsPlaying) _mp.Stop(); } catch { /* robust */ }
                SafeDisposeMediaLocked(sid);

                _pendingSeekMs = startMs is > 0 ? startMs : null;

                _media = CreateMedia(_lib, url);

                // Hint für Startoffset (manche Inputs springen so schneller)
                if (_pendingSeekMs is long ms && ms >= 1000)
                {
                    var secs = (int)(ms / 1000);
                    try
                    {
                        _media.AddOption($":start-time={secs}");
                        _media.AddOption(":input-fast-seek");
                    }
                    catch { /* optional */ }
                }

                var ok = _mp.Play(_media);

                // Sichtbar: erst „Loading“, bis echte Signale eintreffen
                State.IsPlaying = false;
                State.Position = TimeSpan.Zero;
                State.Length = null;
                State.Capabilities = Capabilities;
                SafeFire();

                if (!ok)
                    Log.Debug("[#{sid}] _mp.Play returned false");
            }
        }

        public void TogglePause()
        {
            lock (_sync)
            {
                try
                {
                    if (_mp.CanPause) _mp.Pause();
                    else _mp.Play();

                    State.IsPlaying = _mp.IsPlaying;
                }
                catch { /* robust */ }

                SafeFire();
            }
        }

        public void SeekRelative(TimeSpan delta)
        {
            lock (_sync)
            {
                try
                {
                    var target = State.Position + delta;
                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    SeekTo(target);
                }
                catch { /* ignore */ }
            }
        }

        public void SeekTo(TimeSpan position)
        {
            lock (_sync)
            {
                try
                {
                    var ms = (long)Math.Max(0, position.TotalMilliseconds);
                    if (_mp.IsSeekable)
                    {
                        _mp.Time = ms;
                    }
                    else
                    {
                        var len = _mp.Length;
                        if (len > 0)
                            _mp.Position = Math.Clamp((float)ms / len, 0f, 1f);
                    }
                }
                catch { /* ignore */ }
            }
        }

        public void SetVolume(int vol0to100)
        {
            lock (_sync)
            {
                try
                {
                    var v = Math.Clamp(vol0to100, 0, 100);
                    _mp.Volume = v;
                    State.Volume0_100 = v;
                }
                catch { /* ignore */ }

                SafeFire();
            }
        }

        public void SetSpeed(double speed)
        {
            lock (_sync)
            {
                try
                {
                    var s = Math.Clamp(speed, 0.25, 3.0);
                    _mp.SetRate((float)s);
                    State.Speed = s;
                }
                catch { /* ignore */ }

                SafeFire();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                try { if (_mp.IsPlaying) _mp.Stop(); } catch { }
                SafeDisposeMediaLocked(_sessionId);

                State.IsPlaying = false;
                SafeFire();
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _mp.Dispose(); } catch { }
            try { _lib.Dispose(); } catch { }
        }

        // ----------------- intern -----------------

        private static VLC.Media CreateMedia(VLC.LibVLC lib, string input)
        {
            // 1) Echte URL?
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
                return new VLC.Media(lib, uri.ToString(), VLC.FromType.FromLocation);

            // 2) Lokaler Pfad?
            if (File.Exists(input))
                return new VLC.Media(lib, input, VLC.FromType.FromPath);

            // 3) Fallback: als Location versuchen
            return new VLC.Media(lib, input, VLC.FromType.FromLocation);
        }

        private void SafeDisposeMediaLocked(int sid)
        {
            try { _media?.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "[#{sid}] media.Dispose"); }
            _media = null;
        }

        private void OnPlaying(object? _, EventArgs __)
        {
            lock (_sync)
            {
                try
                {
                    // Pending-Seek einmalig nachreichen (stabil nach Playing)
                    var want = _pendingSeekMs;
                    _pendingSeekMs = null;
                    if (want is long ms && ms > 0)
                    {
                        try
                        {
                            if (_mp.IsSeekable) _mp.Time = ms;
                            else if (_mp.Length > 0) _mp.Position = Math.Clamp((float)ms / _mp.Length, 0f, 1f);
                        }
                        catch (Exception ex) { Log.Debug(ex, "pending seek failed"); }
                    }

                    if (!_ready)
                    {
                        var len = _mp.Length;
                        var pos = _mp.Time;
                        if (len > 0 || pos > 0)
                        {
                            _ready = true;
                            State.IsPlaying = true;
                            SafeFire();
                        }
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "OnPlaying"); }
            }
        }

        private void OnTimeChanged(object? _, VLC.MediaPlayerTimeChangedEventArgs e)
        {
            lock (_sync)
            {
                try
                {
                    State.Position = TimeSpan.FromMilliseconds(e.Time);

                    var len = _mp.Length;
                    if (len > 0) State.Length = TimeSpan.FromMilliseconds(len);

                    if (!_ready && (e.Time > 0 || len > 0))
                    {
                        _ready = true;
                        State.IsPlaying = true;
                    }

                    // nahe Ende: wenn nicht (mehr) playing und Zeit ≈ Länge
                    if (len > 0 && !_mp.IsPlaying && e.Time >= Math.Max(0, len - 250))
                        State.IsPlaying = false;

                    SafeFire();
                }
                catch (Exception ex) { Log.Debug(ex, "OnTimeChanged"); }
            }
        }

        private void OnLengthChanged(object? _, VLC.MediaPlayerLengthChangedEventArgs e)
        {
            lock (_sync)
            {
                try
                {
                    State.Length = e.Length > 0
                        ? TimeSpan.FromMilliseconds(e.Length)
                        : null;

                    if (!_ready && e.Length > 0)
                        State.IsPlaying = _ready = true;

                    SafeFire();
                }
                catch (Exception ex) { Log.Debug(ex, "OnLengthChanged"); }
            }
        }

        private void OnEndReached(object? _, EventArgs __)
        {
            lock (_sync)
            {
                try
                {
                    var len = _mp.Length;
                    if (len > 0)
                    {
                        State.Length = TimeSpan.FromMilliseconds(len);
                        State.Position = TimeSpan.FromMilliseconds(len);
                    }
                    State.IsPlaying = false;
                    SafeFire();
                }
                catch (Exception ex) { Log.Debug(ex, "OnEndReached"); }
            }
        }

        private void OnEncounteredError(object? _, EventArgs __)
        {
            lock (_sync)
            {
                try
                {
                    State.IsPlaying = false;
                    SafeFire();
                }
                catch (Exception ex) { Log.Debug(ex, "OnEncounteredError"); }
            }
        }

        private void SafeFire()
        {
            try { StateChanged?.Invoke(State); } catch { /* UI darf Player nicht crashen */ }
        }
    }
}
