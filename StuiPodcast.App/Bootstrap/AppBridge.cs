using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Bootstrap;

// Bridges persisted preferences between AppFacade (config + library stores)
// and the runtime AppData state. Feeds/episodes/queue are no longer duplicated
// into AppData — they live solely in LibraryStore and are read through stores.
static class AppBridge
{
    public static void SyncFromFacadeToAppData(AppFacade app, AppData data)
    {
        data.PreferredEngine = app.EnginePreference;
        data.Volume0_100     = app.Volume0100;
        data.Speed           = app.Speed;
        data.ThemePref       = app.Theme;
        data.PlayerAtTop     = app.PlayerAtTop;
        data.UnplayedOnly    = app.UnplayedOnly;
        data.SortBy          = app.SortBy;
        data.SortDir         = app.SortDir;
        data.FeedSortBy      = app.FeedSortBy;
        data.FeedSortDir     = app.FeedSortDir;
    }

    public static void SyncFromAppDataToFacade(AppData data, AppFacade app)
    {
        app.EnginePreference = data.PreferredEngine;
        app.Volume0100       = data.Volume0_100;
        app.Speed            = data.Speed;
        app.Theme            = data.ThemePref ?? app.Theme;
        app.PlayerAtTop      = data.PlayerAtTop;
        app.UnplayedOnly     = data.UnplayedOnly;
        app.SortBy           = data.SortBy;
        app.SortDir          = data.SortDir;
        app.FeedSortBy       = data.FeedSortBy;
        app.FeedSortDir      = data.FeedSortDir;
    }
}
