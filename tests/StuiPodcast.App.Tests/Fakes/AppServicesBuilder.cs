using StuiPodcast.App.Bootstrap;
using StuiPodcast.App.Command.UseCases;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra.Feeds;
using StuiPodcast.Infra.Storage;
using StuiPodcast.Infra.Sync;

namespace StuiPodcast.App.Tests.Fakes;

// Builds a complete AppServices out of fakes so the wiring in UiComposer can
// be exercised as a whole rather than one narrowed overload at a time.
//
// Everything that talks to the outside world is a fake; the few pieces with
// no interface (AppFacade, ConfigStore, LibraryStore, GpodderStore) get a
// throwaway temp directory, which is what the storage tests already do.
//
// Dispose removes that directory.
sealed class AppServicesBuilder : IDisposable
{
    private readonly string _dir;

    public FakeUiShell Ui { get; } = new();
    public AppData Data { get; } = new();
    public FakeEpisodeStore Episodes { get; } = new();
    public FakeFeedStore FeedStore { get; } = new();
    public FakeQueueService Queue { get; } = new();
    public FakeFeedService Feeds { get; } = new();
    public FakeAudioPlayer Player { get; } = new();
    public FakeDownloadManager Downloader { get; } = new();
    public MemoryLogSink MemLog { get; } = new();

    public ConfigStore ConfigStore { get; }
    public LibraryStore LibraryStore { get; }
    public AppFacade App { get; }
    public GpodderStore GpodderStore { get; }
    public PlaybackCoordinator Playback { get; }
    public SaveScheduler Saver { get; }
    public EngineService EngineSvc { get; }
    public CmdCases Cases { get; }
    public NetworkMonitor Net { get; }
    public DownloadLookupAdapter DownloadLookup { get; }

    public int SaveCount { get; private set; }

    // :engine reaches EngineUseCase through the delegate CmdCases was built
    // with, not through anything WireUi passes.
    public readonly List<AudioEngine> EngineSwitches = new();

    public AppServicesBuilder()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-services-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        ConfigStore = new ConfigStore(_dir);
        LibraryStore = new LibraryStore(_dir, subFolder: "", fileName: "library.json");
        ConfigStore.Load();
        LibraryStore.Load();

        DownloadLookup = new DownloadLookupAdapter(Downloader, Data);
        App = new AppFacade(ConfigStore, LibraryStore, DownloadLookup);
        GpodderStore = new GpodderStore(_dir);

        Playback = new PlaybackCoordinator(Data, Player, Save, MemLog, Episodes, Queue, FeedStore);
        Saver = new SaveScheduler(Data, App, () => { });
        EngineSvc = new EngineService(Data, MemLog);

        Cases = new CmdCases(
            ui: Ui, data: Data, persist: Save,
            episodes: Episodes, feedStore: FeedStore, queue: Queue,
            audioPlayer: Player, playback: Playback, dlm: Downloader,
            switchEngine: e => { EngineSwitches.Add(e); return Task.CompletedTask; }, sync: null,
            sleepTimer: new SleepTimer(() => { }),
            chaptersFetcher: new ChaptersFetcher(new FakeHttpHandler()));

        Net = new NetworkMonitor(Data, Ui, Save, Episodes, Cases.View);
    }

    public Task Save() { SaveCount++; return Task.CompletedTask; }

    public AppServices Build() => new(
        Ui: Ui, Data: Data, App: App,
        ConfigStore: ConfigStore, LibraryStore: LibraryStore,
        Episodes: Episodes, FeedStore: FeedStore, Queue: Queue,
        Feeds: Feeds, Player: Player, Playback: Playback,
        Downloader: Downloader, DownloadLookup: DownloadLookup,
        MemLog: MemLog, GpodderStore: GpodderStore, Gpodder: null,
        Saver: Saver, Net: Net, EngineSvc: EngineSvc, Cases: Cases);

    public void Dispose()
    {
        try { Playback.Dispose(); } catch { }
        try { App.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
