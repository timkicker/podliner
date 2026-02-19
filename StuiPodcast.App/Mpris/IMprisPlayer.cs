using Tmds.DBus;

namespace StuiPodcast.App.Mpris;

[DBusInterface("org.mpris.MediaPlayer2.Player")]
interface IMprisPlayer : IDBusObject
{
    Task NextAsync();
    Task PreviousAsync();
    Task PauseAsync();
    Task PlayPauseAsync();
    Task StopAsync();
    Task PlayAsync();
    Task SeekAsync(long offsetUs);
    Task SetPositionAsync(ObjectPath trackId, long posUs);
    Task OpenUriAsync(string uri);
    Task<IDisposable> WatchSeekedAsync(Action<long> handler, Action<Exception> onError);
    Task<object> GetAsync(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}
