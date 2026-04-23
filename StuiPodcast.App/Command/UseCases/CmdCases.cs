using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Player;

namespace StuiPodcast.App.Command.UseCases;

// Aggregate container for every command-level UseCase. Constructed once in
// Program.cs after every store and service exists; handed into CmdContext
// so command handlers and the command-router fastpaths can access any
// UseCase via `ctx.Cases.XXX`. Construction order honours the internal
// dep graph: ViewUseCase is created first because History/State/Net/
// Download all wire it in for list refreshes after they mutate state.
internal sealed class CmdCases
{
    public ViewUseCase       View       { get; }
    public HistoryUseCase    History    { get; }
    public StateUseCase      State      { get; }
    public NetUseCase        Net        { get; }
    public IoUseCase         Io         { get; }
    public FeedUseCase       Feed       { get; }
    public NavigationUseCase Navigation { get; }
    public QueueUseCase      Queue      { get; }
    public DownloadUseCase   Download   { get; }
    public OpmlUseCase       Opml       { get; }
    public TransportUseCase  Transport  { get; }
    public EngineUseCase     Engine     { get; }
    public SyncUseCase       Sync       { get; }
    public SystemUseCase     System     { get; }
    public SleepUseCase      Sleep      { get; }
    public UndoUseCase       Undo       { get; }
    public UndoStack         UndoStack  { get; }
    public ChaptersUseCase   Chapters   { get; }

    public CmdCases(
        IUiShell ui,
        AppData data,
        Func<Task> persist,
        IEpisodeStore episodes,
        IFeedStore feedStore,
        IQueueService queue,
        IAudioPlayer audioPlayer,
        PlaybackCoordinator playback,
        DownloadManager dlm,
        Func<AudioEngine, Task>? switchEngine,
        GpodderSyncService? sync,
        SleepTimer sleepTimer,
        StuiPodcast.Infra.Feeds.ChaptersFetcher chaptersFetcher)
    {
        // View first — every list-mutating UseCase depends on it for the
        // post-mutation refresh.
        View       = new ViewUseCase(ui, data, persist, episodes, feedStore);
        History    = new HistoryUseCase(ui, data, persist, episodes, View);
        State      = new StateUseCase(ui, persist, episodes, View);
        Net        = new NetUseCase(ui, data, persist, episodes, View);
        Io         = new IoUseCase(ui, feedStore);
        Feed       = new FeedUseCase(ui, data, persist, episodes, feedStore);
        Navigation = new NavigationUseCase(ui, data, episodes, playback);
        Queue      = new QueueUseCase(ui, persist, episodes, queue);
        Download   = new DownloadUseCase(ui, persist, episodes, dlm, View);
        Opml       = new OpmlUseCase(ui, persist, feedStore);
        Transport  = new TransportUseCase(audioPlayer, ui, data, persist, episodes);
        Engine     = new EngineUseCase(audioPlayer, ui, data, persist, switchEngine);
        Sync       = new SyncUseCase(ui, sync);
        System     = new SystemUseCase(ui, persist);
        Sleep      = new SleepUseCase(ui, sleepTimer);
        UndoStack  = new UndoStack();
        Undo       = new UndoUseCase(ui, UndoStack);
        Queue      = new QueueUseCase(ui, persist, episodes, queue, UndoStack);
        Chapters   = new ChaptersUseCase(ui, audioPlayer, episodes, chaptersFetcher, persist,
            localPathLookup: id => Bootstrap.Program.TryGetLocalPath(id, out var p) ? p : null);
    }
}
