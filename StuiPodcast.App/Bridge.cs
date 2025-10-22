using StuiPodcast.Core;
using StuiPodcast.Infra;

namespace StuiPodcast.App;

// ==========================================================
// Bridge: AppFacade <-> AppData
// ==========================================================
static class Bridge
{
    public static void SyncFromFacadeToAppData(AppFacade app, AppData data)
    {
        // Config → AppData
        data.PreferredEngine = app.EnginePreference;
        data.Volume0_100     = app.Volume0_100;
        data.Speed           = app.Speed;
        data.ThemePref       = app.Theme;
        data.PlayerAtTop     = app.PlayerAtTop;
        data.UnplayedOnly    = app.UnplayedOnly;
        data.SortBy          = app.SortBy;
        data.SortDir         = app.SortDir;
        data.PlaySource      = data.PlaySource ?? "auto";

        // Inhalte
        data.Feeds.Clear();    data.Feeds.AddRange(app.Feeds);
        data.Episodes.Clear(); data.Episodes.AddRange(app.Episodes);
        data.Queue.Clear();    data.Queue.AddRange(app.Queue);
    }

    public static void SyncFromAppDataToFacade(AppData data, AppFacade app)
    {
        // Config
        app.EnginePreference = data.PreferredEngine;
        app.Volume0_100      = data.Volume0_100;
        app.Speed            = data.Speed;
        app.Theme            = data.ThemePref ?? app.Theme;
        app.PlayerAtTop      = data.PlayerAtTop;
        app.UnplayedOnly     = data.UnplayedOnly;
        app.SortBy           = data.SortBy  ?? app.SortBy;
        app.SortDir          = data.SortDir ?? app.SortDir;

        // Inhalte → Queue (Snapshot)
        foreach (var q in app.Queue.ToList()) app.QueueRemove(q);
        foreach (var id in data.Queue) app.QueuePush(id);
        // Saved / Progress / History werden anderweitig gepflegt (Coordinator/Router)
    }
}