using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command;

internal sealed class CmdContext
{
    public IPlayer Player { get; }
    public PlaybackCoordinator Playback { get; }
    public UiShell UI { get; }
    public MemoryLogSink Mem { get; }
    public AppData Data { get; }
    public Func<Task> Persist { get; }
    public DownloadManager Dlm { get; }
    public Func<string, Task>? SwitchEngine { get; }

    public CmdContext(IPlayer player, PlaybackCoordinator playback, UiShell ui, MemoryLogSink mem,
        AppData data, Func<Task> persist, DownloadManager dlm, Func<string, Task>? switchEngine)
    {
        Player = player; Playback = playback; UI = ui; Mem = mem;
        Data = data; Persist = persist; Dlm = dlm; SwitchEngine = switchEngine;
    }
}
