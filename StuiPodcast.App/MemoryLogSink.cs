using System;
using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace StuiPodcast.App.Debug
{
    public sealed class MemoryLogSink : ILogEventSink
    {
        #region fields
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly int _capacity;
        #endregion

        #region ctor
        public MemoryLogSink(int capacity = 2000)
            => _capacity = Math.Max(100, capacity);
        #endregion

        #region public api
        public void Emit(LogEvent logEvent)
        {
            var ts  = logEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
            var lvl = logEvent.Level.ToString()[..3].ToUpperInvariant();
            var msg = logEvent.RenderMessage();
            var exc = logEvent.Exception is null ? "" : $"  {logEvent.Exception}";
            var line = $"{ts} [{lvl}] {msg}{exc}";

            _lines.Enqueue(line);
            while (_lines.Count > _capacity && _lines.TryDequeue(out _)) { }
        }

        public string[] Snapshot(int last = 500)
        {
            var arr = _lines.ToArray();
            if (arr.Length <= last) return arr;
            return arr[^last..];
        }
        #endregion
    }
}