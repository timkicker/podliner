using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.MediaFoundation;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Player
{
    // windows fallback using media foundation + naudio
    // supports: play, pause, stop, seek, volume
    // speed not supported (state remains 1.0)
    public sealed class MediaFoundationAudioPlayer : IAudioPlayer
    {
        #region metadata
        public string Name => "MediaFoundation";

        // current state + event
        public PlayerState State => _state;
        public event Action<PlayerState>? StateChanged;
        public PlayerCapabilities Capabilities { get; } =
            PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
            PlayerCapabilities.Seek | PlayerCapabilities.Volume |
            PlayerCapabilities.Local | PlayerCapabilities.Network;
        #endregion

        #region fields
        readonly object _gate = new();
        WaveOutEvent? _output;
        MediaFoundationReader? _reader;
        VolumeSampleProvider? _vol;
        string? _currentSource;

        PlayerState _state = new PlayerState
        {
            Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Pause | PlayerCapabilities.Stop |
                           PlayerCapabilities.Seek | PlayerCapabilities.Volume |
                           PlayerCapabilities.Local | PlayerCapabilities.Network,
            Volume0_100 = 70,
            Speed = 1.0,
            Position = TimeSpan.Zero,
            Length = TimeSpan.Zero
        };
        #endregion

        #region ctor
        public MediaFoundationAudioPlayer()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MediaFoundationAudioPlayer only available on windows.");
            _state.Capabilities = Capabilities;
            MediaFoundationApi.Startup();
        }
        #endregion

        #region public api
        public void Play(string source, long? startMs = null)
        {
            lock (_gate)
            {
                // reopen when source changed or nothing open
                if (_reader == null || !string.Equals(_currentSource, source, StringComparison.OrdinalIgnoreCase))
                {
                    TearDownLocked();

                    _reader = CreateReader(source);
                    _currentSource = source;

                    var sp = _reader.ToSampleProvider();
                    _vol = new VolumeSampleProvider(sp) { Volume = MapVol(_state.Volume0_100) };

                    _output = new WaveOutEvent { DesiredLatency = 200 };
                    _output.Init(_vol);
                    _output.PlaybackStopped += (_, _) =>
                    {
                        lock (_gate)
                        {
                            _state.IsPlaying = false;
                            SyncPosLocked();
                            Raise();
                        }
                    };

                    _state.Length = SafeLength(_reader);
                    _state.Position = TimeSpan.Zero;

                    if (startMs is { } s && s > 0) SeekCoreLocked(TimeSpan.FromMilliseconds(s));
                }

                _output!.Play();
                _state.IsPlaying = true;
                Raise();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_output != null) _output.Stop();
                SeekCoreLocked(TimeSpan.Zero);
                _state.IsPlaying = false;
                Raise();
            }
        }

        public void TogglePause()
        {
            lock (_gate)
            {
                if (_output == null) return;
                if (_state.IsPlaying) { _output.Pause(); _state.IsPlaying = false; }
                else { _output.Play(); _state.IsPlaying = true; }
                Raise();
            }
        }

        public void SeekRelative(TimeSpan delta)
        {
            lock (_gate)
            {
                if (_reader == null) return;
                var target = _reader.CurrentTime + delta;
                if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                var len = SafeLength(_reader);
                if (len.HasValue && target > len.Value) target = len.Value;
                SeekCoreLocked(target);
                Raise();
            }
        }

        public void SeekTo(TimeSpan pos)
        {
            lock (_gate)
            {
                if (_reader == null) return;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                var len = SafeLength(_reader);
                if (len.HasValue && pos > len.Value) pos = len.Value;
                SeekCoreLocked(pos);
                Raise();
            }
        }

        public void SetVolume(int volume0100)
        {
            lock (_gate)
            {
                _state.Volume0_100 = Math.Clamp(volume0100, 0, 100);
                if (_vol != null) _vol.Volume = MapVol(_state.Volume0_100);
                Raise();
            }
        }

        public void SetSpeed(double speed)
        {
            // not supported; keep state at 1.0 for ui consistency
            lock (_gate)
            {
                _state.Speed = 1.0;
                Raise();
            }
        }
        #endregion

        #region helpers
        MediaFoundationReader CreateReader(string source)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return new MediaFoundationReader(source);

            if (!File.Exists(source))
                throw new FileNotFoundException("Audiosource not found", source);

            return new MediaFoundationReader(source);
        }

        void SeekCoreLocked(TimeSpan pos)
        {
            try
            {
                if (_reader != null && _reader.CanSeek)
                {
                    _reader.CurrentTime = pos;
                    _state.Position = pos;
                    _state.Length = SafeLength(_reader);
                }
            }
            catch { /* best effort */ }
        }

        void SyncPosLocked()
        {
            try
            {
                if (_reader != null)
                {
                    _state.Position = _reader.CurrentTime;
                    _state.Length = SafeLength(_reader);
                }
            }
            catch
            {
                // ignored
            }
        }

        static TimeSpan? SafeLength(MediaFoundationReader r)
        {
            try { return r.TotalTime; } catch { return null; }
        }

        static float MapVol(int v) => (float)(Math.Clamp(v, 0, 100) / 100.0);

        void TearDownLocked()
        {
            try { _output?.Stop(); }
            catch
            {
                // ignored
            }

            try { _output?.Dispose(); }
            catch
            {
                // ignored
            }

            _output = null;

            try { _reader?.Dispose(); }
            catch
            {
                // ignored
            }

            _reader = null;

            _vol = null;
            _currentSource = null;

            _state.IsPlaying = false;
            _state.Position = TimeSpan.Zero;
            _state.Length = TimeSpan.Zero;
        }

        void Raise() => StateChanged?.Invoke(_state);
        #endregion

        #region dispose
        public void Dispose()
        {
            lock (_gate) TearDownLocked();
            try { MediaFoundationApi.Shutdown(); }
            catch
            {
                // ignored
            }

            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
