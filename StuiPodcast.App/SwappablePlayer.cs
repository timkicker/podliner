using System;
using System.Threading.Tasks;
using StuiPodcast.Core;
using StuiPodcast.Infra;

namespace StuiPodcast.App
{
    /// <summary>
    /// Stabiler Proxy für IPlayer, dessen innere Engine zur Laufzeit austauschbar ist.
    /// Thread-safe: Öffentliche Methoden greifen auf einen Snapshot der aktuellen Engine zu.
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

        // Snapshot-Helfer: gibt die aktuell aktive Engine atomar zurück
        private IPlayer Inner
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

        public void Play(string url, long? startMs = null)
        {
            // Snapshot, dann aufrufen
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

        public void Dispose()
        {
            IPlayer old;
            lock (_gate)
            {
                old = _inner;
                _inner.StateChanged -= ForwardState;
                // Kein Null setzen – wir behalten eine gültige Referenz bis Dispose durch ist
            }

            try { old.Dispose(); } catch { /* robust */ }
        }

        /// <summary>
        /// Tauscht die Engine im Laufenden Betrieb aus. Optionaler Hook vor Dispose der alten Engine.
        /// </summary>
        public async Task SwapToAsync(IPlayer next, Action<IPlayer>? onBeforeDispose = null)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));

            IPlayer old;
            lock (_gate)
            {
                old = _inner;
                try { old.StateChanged -= ForwardState; } catch { }
                _inner = next;
                _inner.StateChanged += ForwardState;
            }

            try { onBeforeDispose?.Invoke(old); } catch { /* ignore */ }
            await Task.Yield(); // sanfter Kontextwechsel
            try { old.Dispose(); } catch { /* robust */ }
        }

        private void ForwardState(PlayerState s)
        {
            try { StateChanged?.Invoke(s); } catch { /* UI darf Player nicht crashen */ }
        }
    }
}
