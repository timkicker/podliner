using StuiPodcast.App.Debug;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command;

internal sealed class CmdContext
{
    public IAudioPlayer AudioPlayer { get; }
    public PlaybackCoordinator Playback { get; }
    public UiShell UI { get; }
    public MemoryLogSink Mem { get; }
    public AppData Data { get; }
    public Func<Task> Persist { get; }
    public DownloadManager Dlm { get; }
    public Func<string, Task>? SwitchEngine { get; }

    public CmdContext(IAudioPlayer audioPlayer, PlaybackCoordinator playback, UiShell ui, MemoryLogSink mem,
        AppData data, Func<Task> persist, DownloadManager dlm, Func<string, Task>? switchEngine)
    {
        AudioPlayer = audioPlayer; Playback = playback; UI = ui; Mem = mem;
        Data = data; Persist = persist; Dlm = dlm; SwitchEngine = switchEngine;
    }
}
