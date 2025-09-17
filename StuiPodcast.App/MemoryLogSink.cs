namespace StuiPodcast.App.Debug;

public sealed class MemoryLogSink : Serilog.Core.ILogEventSink
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private readonly int _capacity;

    public MemoryLogSink(int capacity = 2000) => _capacity = System.Math.Max(100, capacity);

    public void Emit(Serilog.Events.LogEvent logEvent)
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
}