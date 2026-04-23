using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command;

internal sealed class CmdContext
{
    public IAudioPlayer AudioPlayer { get; }
    public PlaybackCoordinator Playback { get; }
    public IUiShell Ui { get; }
    public MemoryLogSink Mem { get; }
    public AppData Data { get; }
    public Func<Task> Persist { get; }
    public DownloadManager Dlm { get; }
    public Func<AudioEngine, Task>? SwitchEngine { get; }
    public GpodderSyncService? Sync { get; }
    public IEpisodeStore Episodes { get; }
    public IFeedStore    FeedStore { get; }
    public IQueueService Queue { get; }
    public CmdCases Cases { get; }

    public CmdContext(IAudioPlayer audioPlayer, PlaybackCoordinator playback, IUiShell ui, MemoryLogSink mem,
        AppData data, Func<Task> persist, DownloadManager dlm, Func<AudioEngine, Task>? switchEngine,
        IEpisodeStore episodes, IFeedStore feedStore, IQueueService queue,
        CmdCases cases,
        GpodderSyncService? sync = null)
    {
        AudioPlayer = audioPlayer; Playback = playback; Ui = ui; Mem = mem;
        Data = data; Persist = persist; Dlm = dlm; SwitchEngine = switchEngine; Sync = sync;
        Episodes  = episodes  ?? throw new ArgumentNullException(nameof(episodes));
        FeedStore = feedStore ?? throw new ArgumentNullException(nameof(feedStore));
        Queue     = queue     ?? throw new ArgumentNullException(nameof(queue));
        Cases     = cases     ?? throw new ArgumentNullException(nameof(cases));
    }
}
