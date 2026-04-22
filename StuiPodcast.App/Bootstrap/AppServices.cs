using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using StuiPodcast.Infra;
using StuiPodcast.Infra.Download;
using StuiPodcast.Infra.Storage;
using StuiPodcast.Infra.Sync;

namespace StuiPodcast.App.Bootstrap;

// Composition-root record: holds every service the wiring code and command
// pipeline needs. Constructed once in Program.Main after all services are
// built, then passed to UiComposer.WireUi and similar orchestrators.
//
// Replaces the previous pattern of UiComposer reflecting into Program's
// private static fields, which was fragile (renaming a field silently broke
// UI wiring at runtime).
//
// Nullable reference semantics: fields are non-null by the time WireUi runs.
// Rather than pepper WireUi with null checks, we assert here and rely on the
// ordering in Program.Main (all services built before UiComposer is called).
internal sealed record AppServices(
    UiShell               Ui,
    AppData               Data,
    AppFacade             App,
    ConfigStore           ConfigStore,
    LibraryStore          LibraryStore,
    IEpisodeStore         Episodes,
    FeedService           Feeds,
    SwappableAudioPlayer  Player,
    PlaybackCoordinator   Playback,
    DownloadManager       Downloader,
    DownloadLookupAdapter DownloadLookup,
    MemoryLogSink         MemLog,
    GpodderStore          GpodderStore,
    GpodderSyncService?   Gpodder,
    SaveScheduler         Saver,
    NetworkMonitor        Net,
    EngineService         EngineSvc
);
