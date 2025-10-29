using System.Collections.ObjectModel;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    // app facade: single entry point for ui and orchestration
    // wraps config store and library store; downloads are not persisted
    public sealed class AppFacade
    {
        public ConfigStore  ConfigStore  { get; }
        public LibraryStore LibraryStore { get; }
        public ILocalDownloadLookup Downloads { get; }

        #region events
        // coalesced persistence changes; ui can observe
        public event Action? Changed;
        #endregion

        #region constructor
        public AppFacade(ConfigStore configStore, LibraryStore libraryStore, ILocalDownloadLookup? downloadLookup = null)
        {
            ConfigStore  = configStore  ?? throw new ArgumentNullException(nameof(configStore));
            LibraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            Downloads    = downloadLookup ?? NullDownloadLookup.Instance;

            // forward change events from both stores
            ConfigStore.Changed  += () => Changed?.Invoke();
            LibraryStore.Changed += () => Changed?.Invoke();
        }
        #endregion

        #region load and save
        // load order: config first, then library
        public AppConfig LoadConfig()  => ConfigStore.Load();
        public Library   LoadLibrary() => LibraryStore.Load();

        // immediate save for explicit write or exit
        public void SaveNow()
        {
            ConfigStore.SaveNow();
            LibraryStore.SaveNow();
        }
        #endregion

        #region config properties
        public string? EnginePreference
        {
            get => ConfigStore.Current.EnginePreference;
            set { ConfigStore.Current.EnginePreference = value ?? "auto"; ConfigStore.SaveAsync(); }
        }

        public int Volume0100
        {
            get => ConfigStore.Current.Volume0_100;
            set { ConfigStore.Current.Volume0_100 = value; ConfigStore.SaveAsync(); }
        }

        public double Speed
        {
            get => ConfigStore.Current.Speed;
            set { ConfigStore.Current.Speed = value; ConfigStore.SaveAsync(); }
        }

        public string? Theme
        {
            get => ConfigStore.Current.Theme;
            set { ConfigStore.Current.Theme = value ?? "Base"; ConfigStore.SaveAsync(); }
        }

        public bool PlayerAtTop
        {
            get => ConfigStore.Current.Ui.PlayerAtTop;
            set { ConfigStore.Current.Ui.PlayerAtTop = value; ConfigStore.SaveAsync(); }
        }

        public string? SortBy
        {
            get => ConfigStore.Current.ViewDefaults.SortBy;
            set { ConfigStore.Current.ViewDefaults.SortBy = value ?? "pubdate"; ConfigStore.SaveAsync(); }
        }

        public string? SortDir
        {
            get => ConfigStore.Current.ViewDefaults.SortDir;
            set { ConfigStore.Current.ViewDefaults.SortDir = value ?? "desc"; ConfigStore.SaveAsync(); }
        }

        public bool UnplayedOnly
        {
            get => ConfigStore.Current.ViewDefaults.UnplayedOnly;
            set { ConfigStore.Current.ViewDefaults.UnplayedOnly = value; ConfigStore.SaveAsync(); }
        }
        #endregion

        #region read only views
        // ui can enumerate these directly
        public IReadOnlyList<Feed> Feeds => new ReadOnlyCollection<Feed>(LibraryStore.Current.Feeds);
        public IReadOnlyList<Episode> Episodes => new ReadOnlyCollection<Episode>(LibraryStore.Current.Episodes);
        public IReadOnlyList<Guid> Queue => new ReadOnlyCollection<Guid>(LibraryStore.Current.Queue);
        public IReadOnlyList<HistoryItem> History => new ReadOnlyCollection<HistoryItem>(LibraryStore.Current.History);

        // fast episode access
        public Episode GetEpisodeOrThrow(Guid episodeId) => LibraryStore.GetEpisodeOrThrow(episodeId);
        #endregion

        #region mutations
        // changes are forwarded to the library store which batches writes
        public Feed AddOrUpdateFeed(Feed feed) => LibraryStore.AddOrUpdateFeed(feed);
        public Episode AddOrUpdateEpisode(Episode ep) => LibraryStore.AddOrUpdateEpisode(ep);

        public void SetEpisodeProgress(Guid episodeId, long lastPosMs, DateTimeOffset? lastPlayedAtUtc)
            => LibraryStore.SetEpisodeProgress(episodeId, lastPosMs, lastPlayedAtUtc);

        public void SetSaved(Guid episodeId, bool saved)
            => LibraryStore.SetSaved(episodeId, saved);

        // queue operations
        public void QueuePush(Guid episodeId) => LibraryStore.QueuePush(episodeId);
        public bool QueueRemove(Guid episodeId) => LibraryStore.QueueRemove(episodeId);
        public void QueueTrimBefore(Guid episodeId) => LibraryStore.QueueTrimBefore(episodeId);
        public void QueueClear() => LibraryStore.QueueClear();

        // history operations
        public void HistoryAdd(Guid episodeId, DateTimeOffset atUtc) => LibraryStore.HistoryAdd(episodeId, atUtc);
        public void HistoryClear() => LibraryStore.HistoryClear();
        
        // remove
        public bool RemoveFeed(Guid feedId) 
            => LibraryStore.RemoveFeed(feedId);

        public int RemoveEpisodesByFeed(Guid feedId) 
            => LibraryStore.RemoveEpisodesByFeed(feedId);

        public int QueueRemoveByEpisodeIds(IEnumerable<Guid> episodeIds) 
            => LibraryStore.QueueRemoveByEpisodeIds(episodeIds);
        #endregion

        #region downloads
        // true if a local file exists in the standard podcasts folder
        // source is the download manager or the file system, not the library
        public bool IsDownloaded(Guid episodeId) => Downloads.IsDownloaded(episodeId);

        // returns local path if present; no persistence
        public bool TryGetLocalPath(Guid episodeId, out string? path) => Downloads.TryGetLocalPath(episodeId, out path);
        #endregion

        #region lookup types
        // read only adapter for a download manager or file system
        public interface ILocalDownloadLookup
        {
            bool IsDownloaded(Guid episodeId);
            bool TryGetLocalPath(Guid episodeId, out string? path);
        }

        // fallback when no adapter is provided
        sealed class NullDownloadLookup : ILocalDownloadLookup
        {
            public static readonly NullDownloadLookup Instance = new();
            private NullDownloadLookup() { }
            public bool IsDownloaded(Guid episodeId) => false;
            public bool TryGetLocalPath(Guid episodeId, out string? path) { path = null; return false; }
        }
        #endregion
    }
}
