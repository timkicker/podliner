using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App;

internal sealed class CommandContext
{
    public IPlayer Player { get; }
    public PlaybackCoordinator Playback { get; }
    public Shell UI { get; }
    public MemoryLogSink Mem { get; }
    public AppData Data { get; }
    public Func<Task> Persist { get; }
    public DownloadManager Dlm { get; }
    public Func<string, Task>? SwitchEngine { get; }

    public CommandContext(IPlayer player, PlaybackCoordinator playback, Shell ui, MemoryLogSink mem,
        AppData data, Func<Task> persist, DownloadManager dlm, Func<string, Task>? switchEngine)
    {
        Player = player; Playback = playback; UI = ui; Mem = mem;
        Data = data; Persist = persist; Dlm = dlm; SwitchEngine = switchEngine;
    }
}
