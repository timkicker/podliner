using Podliner.App.Command.UseCases;
using Podliner.App.Debug;
using Podliner.App.Services;
using Podliner.App.UI;
using Podliner.Core;
using Podliner.Infra;
using Podliner.Infra.Download;
using Podliner.Infra.Player;
using Podliner.Infra.Storage;
using Podliner.Infra.Sync;

namespace Podliner.App.Bootstrap;

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
    IUiShell              Ui,
    AppData               Data,
    AppFacade             App,
    ConfigStore           ConfigStore,
    LibraryStore          LibraryStore,
    IEpisodeStore         Episodes,
    IFeedStore            FeedStore,
    IQueueService         Queue,
    IFeedService          Feeds,
    IAudioPlayer          Player,
    PlaybackCoordinator   Playback,
    IDownloadManager      Downloader,
    AppFacade.ILocalDownloadLookup DownloadLookup,
    MemoryLogSink         MemLog,
    GpodderStore          GpodderStore,
    GpodderSyncService?   Gpodder,
    SaveScheduler         Saver,
    NetworkMonitor        Net,
    EngineService         EngineSvc,
    CmdCases              Cases
);
