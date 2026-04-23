using Serilog;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using Terminal.Gui;

namespace StuiPodcast.App.Command.UseCases;

// :sync orchestration. Network operations are fire-and-forget with explicit
// try/catch so a failed login/push/pull surfaces an OSD with the error
// message instead of silently leaving the "…loading" toast up forever.
internal sealed class SyncUseCase
{
    readonly IUiShell _ui;
    readonly GpodderSyncService? _sync;

    public SyncUseCase(IUiShell ui, GpodderSyncService? sync)
    {
        _ui = ui;
        _sync = sync;
    }

    public void Exec(CmdParsed cmd)
    {
        if (_sync == null)
        {
            _ui.ShowOsd("sync: not available", 2000);
            return;
        }

        var args = cmd.Args;
        var sub  = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        switch (sub)
        {
            case "login":
                if (args.Length < 4)
                {
                    _ui.ShowOsd("usage: :sync login <server> <user> <pass>", 2500);
                    return;
                }
                var server = args[1];
                var user   = args[2];
                var pass   = args[3];
                _ui.ShowOsd("sync: logging in…", 1500);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (ok, msg) = await _sync.LoginAsync(server, user, pass);
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: {msg}", ok ? 3000 : 4000));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "sync login task failed");
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: error: {ex.Message}", 4000));
                    }
                });
                break;

            case "logout":
                _sync.Logout();
                _ui.ShowOsd("sync: logged out", 2000);
                break;

            case "push":
                _ui.ShowOsd("sync: pushing…", 1500);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (ok, msg) = await _sync.PushAsync();
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: {msg}", ok ? 2000 : 3000));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "sync push task failed");
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: error: {ex.Message}", 4000));
                    }
                });
                break;

            case "pull":
                _ui.ShowOsd("sync: pulling…", 1500);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (ok, msg) = await _sync.PullAsync();
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: {msg}", ok ? 2000 : 3000));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "sync pull task failed");
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: error: {ex.Message}", 4000));
                    }
                });
                break;

            case "status":
                _ui.ShowOsd(_sync.GetStatus(), 4000);
                break;

            case "device":
                if (args.Length < 2)
                {
                    _ui.ShowOsd("usage: :sync device <id>", 2000);
                    return;
                }
                _sync.SetDeviceId(args[1]);
                _ui.ShowOsd($"sync: device set to {args[1]}", 2000);
                break;

            case "auto":
                var autoArg = args.Length > 1 ? args[1].ToLowerInvariant() : "toggle";
                bool? value = autoArg switch
                {
                    "on"  => true,
                    "off" => false,
                    _     => null   // toggle
                };
                _sync.SetAutoSync(value);
                var onOff = _sync.ShouldAutoSync ? "on" : "off";
                _ui.ShowOsd($"sync: auto {onOff}", 2000);
                break;

            case "help":
                try
                {
                    var dlg = new Dialog("Sync Help", 80, 28);
                    var tv = new TextView { ReadOnly = true, WordWrap = true, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
                    tv.Text = StuiPodcast.App.HelpCatalog.SyncDoc;
                    dlg.Add(tv);
                    var ok = new Button("OK", is_default: true);
                    ok.Clicked += () => Application.RequestStop();
                    dlg.AddButton(ok);
                    Application.Run(dlg);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "sync help dialog failed");
                    _ui.ShowOsd($"sync: help dialog failed ({ex.Message})", 2000);
                }
                break;

            case "":
                // :sync with no args = full sync
                _ui.ShowOsd("sync: syncing…", 1500);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (ok, msg) = await _sync.SyncAsync();
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: {msg}", ok ? 2000 : 3000));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "sync task failed");
                        Application.MainLoop?.Invoke(() => _ui.ShowOsd($"sync: error: {ex.Message}", 4000));
                    }
                });
                break;

            default:
                _ui.ShowOsd($"sync: unknown sub-command '{sub}'", 2000);
                break;
        }
    }
}
