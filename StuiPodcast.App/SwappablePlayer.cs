using System;
using System.Threading.Tasks;
using StuiPodcast.Core;
using StuiPodcast.Infra;

namespace StuiPodcast.App
{
    /// <summary>
    /// Stabiler Proxy für IPlayer, dessen innerer Engine zur Laufzeit austauschbar ist.
    /// </summary>
    public sealed class SwappablePlayer : IPlayer, IDisposable
    {
        private IPlayer _inner;
        private readonly object _gate = new();

        public SwappablePlayer(IPlayer inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _inner.StateChanged += ForwardState;
        }

        public string Name => _inner.Name;
        public PlayerCapabilities Capabilities => _inner.Capabilities;
        public PlayerState State => _inner.State;

        public event Action<PlayerState>? StateChanged;

        // <- HIER: Signatur wie im Interface (long? statt TimeSpan?)
        public void Play(string url, long? startMs) => _inner.Play(url, startMs);

        public void TogglePause() => _inner.TogglePause();
        public void SeekTo(TimeSpan t) => _inner.SeekTo(t);
        public void SeekRelative(TimeSpan dt) => _inner.SeekRelative(dt);
        public void SetVolume(int v) => _inner.SetVolume(v);
        public void SetSpeed(double s) => _inner.SetSpeed(s);
        public void Stop() => _inner.Stop();

        public void Dispose() => _inner.Dispose();

        public async Task SwapToAsync(IPlayer next, Action<IPlayer>? onBeforeDispose = null)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));

            IPlayer old;
            lock (_gate)
            {
                old = _inner;
                old.StateChanged -= ForwardState;
                _inner = next;
                _inner.StateChanged += ForwardState;
            }

            try { onBeforeDispose?.Invoke(old); } catch { }
            await Task.Yield();
            try { old.Dispose(); } catch { }
        }

        private void ForwardState(PlayerState s)
        {
            try { StateChanged?.Invoke(s); } catch { }
        }
    }
}
