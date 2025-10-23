using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Storage
{
    /// <summary>
    /// App-Façade: ein einziger Einstieg für UI/Orchestrierung.
    /// Kapselt ConfigStore (appsettings.json) + LibraryStore (library.json)
    /// und stellt eine stabile Oberfläche bereit. Downloads werden NICHT persistiert;
    /// "Downloaded?" wird virtuell über ILocalDownloadLookup ermittelt.
    /// </summary>
    public sealed class AppFacade
    {
        public ConfigStore  ConfigStore  { get; }
        public LibraryStore LibraryStore { get; }
        public ILocalDownloadLookup Downloads { get; }

        /// <summary>
        /// Änderungen am Persistenzzustand (koalesziert). UI kann hierauf reagieren.
        /// </summary>
        public event Action? Changed;

        public AppFacade(ConfigStore configStore, LibraryStore libraryStore, ILocalDownloadLookup? downloadLookup = null)
        {
            ConfigStore  = configStore  ?? throw new ArgumentNullException(nameof(configStore));
            LibraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            Downloads    = downloadLookup ?? NullDownloadLookup.Instance;

            // Durchreichen der Changed-Events beider Stores
            ConfigStore.Changed  += () => Changed?.Invoke();
            LibraryStore.Changed += () => Changed?.Invoke();
        }

        // =====================================================================
        // Laden (Start-Reihenfolge bleibt: zuerst Config, dann Library)
        // =====================================================================

        public AppConfig LoadConfig()  => ConfigStore.Load();
        public Library   LoadLibrary() => LibraryStore.Load();

        // =====================================================================
        // SAVE-APIs (UI ruft üblicherweise SaveAsync; :w/Exit → SaveNow)
        // =====================================================================

        /// <summary>Debounced Save – reicht im laufenden Betrieb.</summary>
        public void SaveAsync()
        {
            // Debounced Saves in beiden Stores; die Stores entscheiden selbst,
            // ob sie wirklich schreiben (pending/dirty).
            ConfigStore.SaveAsync();
            LibraryStore.SaveAsync();
        }

        /// <summary>Sofortiges Speichern – für :w / Exit.</summary>
        public void SaveNow()
        {
            ConfigStore.SaveNow();
            LibraryStore.SaveNow();
        }

        // =====================================================================
        // PREFS / CONFIG (Getter/Setter spiegeln AppConfig; persistieren via SaveAsync)
        // =====================================================================

        public string EnginePreference
        {
            get => ConfigStore.Current.EnginePreference;
            set { ConfigStore.Current.EnginePreference = value ?? "auto"; ConfigStore.SaveAsync(); }
        }

        public int Volume0_100
        {
            get => ConfigStore.Current.Volume0_100;
            set { ConfigStore.Current.Volume0_100 = value; ConfigStore.SaveAsync(); }
        }

        public double Speed
        {
            get => ConfigStore.Current.Speed;
            set { ConfigStore.Current.Speed = value; ConfigStore.SaveAsync(); }
        }

        public string Theme
        {
            get => ConfigStore.Current.Theme;
            set { ConfigStore.Current.Theme = value ?? "Base"; ConfigStore.SaveAsync(); }
        }

        public string GlyphSet
        {
            get => ConfigStore.Current.GlyphSet;
            set { ConfigStore.Current.GlyphSet = value ?? "auto"; ConfigStore.SaveAsync(); }
        }

        public NetworkProfile NetworkProfile
        {
            get => ConfigStore.Current.NetworkProfile;
            set { ConfigStore.Current.NetworkProfile = value; ConfigStore.SaveAsync(); }
        }

        public bool StartOffline
        {
            get => ConfigStore.Current.StartOffline;
            set { ConfigStore.Current.StartOffline = value; ConfigStore.SaveAsync(); }
        }

        public bool PlayerAtTop
        {
            get => ConfigStore.Current.Ui.PlayerAtTop;
            set { ConfigStore.Current.Ui.PlayerAtTop = value; ConfigStore.SaveAsync(); }
        }

        public string SortBy
        {
            get => ConfigStore.Current.ViewDefaults.SortBy;
            set { ConfigStore.Current.ViewDefaults.SortBy = value ?? "pubdate"; ConfigStore.SaveAsync(); }
        }

        public string SortDir
        {
            get => ConfigStore.Current.ViewDefaults.SortDir;
            set { ConfigStore.Current.ViewDefaults.SortDir = value ?? "desc"; ConfigStore.SaveAsync(); }
        }

        public bool UnplayedOnly
        {
            get => ConfigStore.Current.ViewDefaults.UnplayedOnly;
            set { ConfigStore.Current.ViewDefaults.UnplayedOnly = value; ConfigStore.SaveAsync(); }
        }

        public string? LastFeedId
        {
            get => ConfigStore.Current.LastSelection.FeedId;
            set { ConfigStore.Current.LastSelection.FeedId = value; ConfigStore.SaveAsync(); }
        }

        public string? LastEpisodeId
        {
            get => ConfigStore.Current.LastSelection.EpisodeId;
            set { ConfigStore.Current.LastSelection.EpisodeId = value; ConfigStore.SaveAsync(); }
        }

        public string LastSearch
        {
            get => ConfigStore.Current.LastSelection.Search;
            set { ConfigStore.Current.LastSelection.Search = value ?? string.Empty; ConfigStore.SaveAsync(); }
        }

        // =====================================================================
        // INHALTE / LIBRARY (Read-Views und Mutationen)
        // =====================================================================

        // Read-Only Views – UI kann direkt enumerieren
        public IReadOnlyList<Feed>    Feeds    => new ReadOnlyCollection<Feed>(LibraryStore.Current.Feeds);
        public IReadOnlyList<Episode> Episodes => new ReadOnlyCollection<Episode>(LibraryStore.Current.Episodes);
        public IReadOnlyList<Guid>    Queue    => new ReadOnlyCollection<Guid>(LibraryStore.Current.Queue);
        public IReadOnlyList<HistoryItem> History => new ReadOnlyCollection<HistoryItem>(LibraryStore.Current.History);

        // Lookups / Convenience
        public bool TryGetEpisode(Guid episodeId, out Episode? ep) => LibraryStore.TryGetEpisode(episodeId, out ep);
        public Episode GetEpisodeOrThrow(Guid episodeId) => LibraryStore.GetEpisodeOrThrow(episodeId);

        // Mutationen – leiten direkt an LibraryStore weiter (der speichert gebatcht)
        public Feed AddOrUpdateFeed(Feed feed) => LibraryStore.AddOrUpdateFeed(feed);
        public Episode AddOrUpdateEpisode(Episode ep) => LibraryStore.AddOrUpdateEpisode(ep);

        public void SetEpisodeProgress(Guid episodeId, long lastPosMs, DateTimeOffset? lastPlayedAtUtc)
            => LibraryStore.SetEpisodeProgress(episodeId, lastPosMs, lastPlayedAtUtc);

        public void SetSaved(Guid episodeId, bool saved)
            => LibraryStore.SetSaved(episodeId, saved);

        // Queue
        public void QueuePush(Guid episodeId) => LibraryStore.QueuePush(episodeId);
        public bool QueueRemove(Guid episodeId) => LibraryStore.QueueRemove(episodeId);
        public void QueueTrimBefore(Guid episodeId) => LibraryStore.QueueTrimBefore(episodeId);
        public void QueueClear() => LibraryStore.QueueClear();

        // History
        public void HistoryAdd(Guid episodeId, DateTimeOffset atUtc) => LibraryStore.HistoryAdd(episodeId, atUtc);
        public void HistoryClear() => LibraryStore.HistoryClear();

        // =====================================================================
        // DOWNLOADS – reines ReadModel (keine Persistenz!)
        // =====================================================================

        /// <summary>
        /// Gibt true zurück, wenn die Episode lokal im Standard-"Podcasts"-Ordner vorhanden ist.
        /// Quelle ist ausschließlich der Download-Manager/Dateisystem, nicht die Library.
        /// </summary>
        public bool IsDownloaded(Guid episodeId) => Downloads.IsDownloaded(episodeId);

        /// <summary>
        /// Liefert den lokalen Pfad (falls vorhanden). Keine Persistenz.
        /// </summary>
        public bool TryGetLocalPath(Guid episodeId, out string? path) => Downloads.TryGetLocalPath(episodeId, out path);

        // =====================================================================
        // Hilfs-Interface & Null-Implementierung für die Download-Abfrage
        // =====================================================================

        /// <summary>
        /// Lesender Adapter auf deinen DownloadManager/Dateisystem.
        /// Implementiere einen kleinen Adapter, der hieran andockt, z. B.:
        /// 
        /// public sealed class DownloadLookupAdapter : ILocalDownloadLookup {
        ///     private readonly DownloadManager _mgr;
        ///     public DownloadLookupAdapter(DownloadManager mgr) { _mgr = mgr; }
        ///     public bool IsDownloaded(Guid episodeId) => _mgr.IsDownloaded(episodeId);
        ///     public bool TryGetLocalPath(Guid episodeId, out string? path) => _mgr.TryGetLocalPath(episodeId, out path);
        /// }
        /// 
        /// Und dann beim Composition Root:
        /// var facade = new AppFacade(cfgStore, libStore, new DownloadLookupAdapter(downloadManager));
        /// </summary>
        public interface ILocalDownloadLookup
        {
            bool IsDownloaded(Guid episodeId);
            bool TryGetLocalPath(Guid episodeId, out string? path);
        }

        /// <summary>
        /// Fallback, falls (noch) kein Download-Adapter bereitsteht:
        /// Immer "nicht geladen".
        /// </summary>
        sealed class NullDownloadLookup : ILocalDownloadLookup
        {
            public static readonly NullDownloadLookup Instance = new();
            private NullDownloadLookup() { }
            public bool IsDownloaded(Guid episodeId) => false;
            public bool TryGetLocalPath(Guid episodeId, out string? path) { path = null; return false; }
        }
    }
}
