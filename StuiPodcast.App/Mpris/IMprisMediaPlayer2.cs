using Tmds.DBus;

namespace StuiPodcast.App.Mpris;

[DBusInterface("org.mpris.MediaPlayer2")]
interface IMprisMediaPlayer2 : IDBusObject
{
    Task RaiseAsync();
    Task QuitAsync();
    Task<object> GetAsync(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}
