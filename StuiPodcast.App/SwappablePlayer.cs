
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App
{
    // stable proxy for iaudioplayer, engine can be swapped at runtime
    public sealed class SwappableAudioPlayer : IAudioPlayer, IDisposable
    {
        #region fields and ctor

        private IAudioPlayer _inner;
        private readonly object _gate = new();

        public SwappableAudioPlayer(IAudioPlayer inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _inner.StateChanged += ForwardState;
        }

        #endregion

        #region properties and events

        // snapshot of current engine
        private IAudioPlayer Inner
        {
            get { lock (_gate) return _inner; }
        }

        public string Name
        {
            get { return Inner.Name; }
        }

        public PlayerCapabilities Capabilities
        {
            get { return Inner.Capabilities; }
        }

        public PlayerState State
        {
            get { return Inner.State; }
        }

        public event Action<PlayerState>? StateChanged;

        #endregion

        #region playback controls

        public void Play(string url, long? startMs = null)
        {
            var p = Inner;
            p.Play(url, startMs);
        }

        public void TogglePause()
        {
            var p = Inner;
            p.TogglePause();
        }

        public void SeekTo(TimeSpan t)
        {
            var p = Inner;
            p.SeekTo(t);
        }

        public void SeekRelative(TimeSpan dt)
        {
            var p = Inner;
            p.SeekRelative(dt);
        }

        public void SetVolume(int v)
        {
            var p = Inner;
            p.SetVolume(v);
        }

        public void SetSpeed(double s)
        {
            var p = Inner;
            p.SetSpeed(s);
        }

        public void Stop()
        {
            var p = Inner;
            p.Stop();
        }

        #endregion

        #region swapping and dispose

        public void Dispose()
        {
            IAudioPlayer old;
            lock (_gate)
            {
                old = _inner;
                _inner.StateChanged -= ForwardState;
            }

            try { old.Dispose(); } catch { }
        }

        // swap engine while running, optional hook before disposing old engine
        public async Task SwapToAsync(IAudioPlayer next, Action<IAudioPlayer>? onBeforeDispose = null)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));

            IAudioPlayer old;
            lock (_gate)
            {
                old = _inner;
                try { old.StateChanged -= ForwardState; } catch { }
                _inner = next;
                _inner.StateChanged += ForwardState;
            }

            try { onBeforeDispose?.Invoke(old); } catch { }
            await Task.Yield();
            try { old.Dispose(); } catch { }
        }

        #endregion

        #region helpers

        private void ForwardState(PlayerState s)
        {
            try { StateChanged?.Invoke(s); } catch { }
        }

        #endregion
    }
}
