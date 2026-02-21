using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Bootstrap;

static class AppBridge
{
    public static void SyncFromFacadeToAppData(AppFacade app, AppData data)
    {
        // config to appdata
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
        data.PlaySource      = data.PlaySource;
        
        data.Feeds.Clear();    data.Feeds.AddRange(app.Feeds);
        data.Episodes.Clear(); data.Episodes.AddRange(app.Episodes);
        data.Queue.Clear();    data.Queue.AddRange(app.Queue);
    }

    public static void SyncFromAppDataToFacade(AppData data, AppFacade app)
    {
        app.EnginePreference = data.PreferredEngine;
        app.Volume0100      = data.Volume0_100;
        app.Speed            = data.Speed;
        app.Theme            = data.ThemePref ?? app.Theme;
        app.PlayerAtTop      = data.PlayerAtTop;
        app.UnplayedOnly     = data.UnplayedOnly;
        app.SortBy           = data.SortBy;
        app.SortDir          = data.SortDir;
        app.FeedSortBy       = data.FeedSortBy;
        app.FeedSortDir      = data.FeedSortDir;
        
        foreach (var q in app.Queue.ToList()) app.QueueRemove(q);
        foreach (var id in data.Queue) app.QueuePush(id);
    }
}