using Serilog;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;

namespace StuiPodcast.App.Services;

// ==========================================================
// Save scheduler (centralized persistence)
// ==========================================================
sealed class SaveScheduler : IDisposable
{
    readonly AppData _data;
    readonly AppFacade _app;
    readonly Action _syncFromDataToFacade;

    readonly object _gate = new();
    DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    bool _pending, _running;
    const int MIN_INTERVAL_MS = 1000;

    public SaveScheduler(AppData data, AppFacade app, Action syncFromDataToFacade)
    {
        _data = data;
        _app = app;
        _syncFromDataToFacade = syncFromDataToFacade;
    }

    // in SaveScheduler
    public Task RequestSaveAsync() => RequestSaveAsync(flush: false);

    
    public async Task RequestSaveAsync(bool flush = false)
    {
        if (flush)
        {
            await SaveNowAsync().ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var since = now - _lastSave;

            if (!_running && since.TotalMilliseconds >= MIN_INTERVAL_MS)
            {
                _running = true;
            }
            else
            {
                ScheduleDelayed();
                return;
            }
        }

        await SaveNowAsync().ConfigureAwait(false);
    }

    void ScheduleDelayed()
    {
        if (_pending) return;
        _pending = true;

        _ = Task.Run(async () =>
        {
            await Task.Delay(MIN_INTERVAL_MS).ConfigureAwait(false);
            lock (_gate)
            {
                _pending = false;
                if (_running) return;
                _running = true;
            }
            _ = SaveNowAsync();
        });
    }

    async Task SaveNowAsync()
    {
        try
        {
            _syncFromDataToFacade();
            _app.SaveNow();
        }
        catch (Exception ex) { Log.Debug(ex, "save failed"); }
        finally
        {
            lock (_gate)
            {
                _lastSave = DateTimeOffset.Now;
                _running = false;
            }
        }
        await Task.CompletedTask;
    }

    public void Dispose() { /* nothing */ }
}