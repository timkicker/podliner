using StuiPodcast.App.Command;
using Terminal.Gui;

namespace StuiPodcast.App;

internal static class CmdSyncModule
{
    public static void HandleSync(CmdParsed cmd, CmdContext ctx)
    {
        var syncService = ctx.Sync;
        var ui          = ctx.Ui;

        if (syncService == null)
        {
            ui.ShowOsd("sync: not available", 2000);
            return;
        }

        var args = cmd.Args;
        var sub  = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        switch (sub)
        {
            case "login":
                if (args.Length < 4)
                {
                    ui.ShowOsd("usage: :sync login <server> <user> <pass>", 2500);
                    return;
                }
                var server = args[1];
                var user   = args[2];
                var pass   = args[3];
                ui.ShowOsd("sync: logging in…", 1500);
                _ = Task.Run(async () =>
                {
                    var (ok, msg) = await syncService.LoginAsync(server, user, pass);
                    Application.MainLoop?.Invoke(() => ui.ShowOsd($"sync: {msg}", ok ? 3000 : 4000));
                });
                break;

            case "logout":
                syncService.Logout();
                ui.ShowOsd("sync: logged out", 2000);
                break;

            case "push":
                ui.ShowOsd("sync: pushing…", 1500);
                _ = Task.Run(async () =>
                {
                    var (ok, msg) = await syncService.PushAsync();
                    Application.MainLoop?.Invoke(() => ui.ShowOsd($"sync: {msg}", ok ? 2000 : 3000));
                });
                break;

            case "pull":
                ui.ShowOsd("sync: pulling…", 1500);
                _ = Task.Run(async () =>
                {
                    var (ok, msg) = await syncService.PullAsync();
                    Application.MainLoop?.Invoke(() => ui.ShowOsd($"sync: {msg}", ok ? 2000 : 3000));
                });
                break;

            case "status":
                ui.ShowOsd(syncService.GetStatus(), 4000);
                break;

            case "device":
                if (args.Length < 2)
                {
                    ui.ShowOsd("usage: :sync device <id>", 2000);
                    return;
                }
                syncService.SetDeviceId(args[1]);
                ui.ShowOsd($"sync: device set to {args[1]}", 2000);
                break;

            case "auto":
                var autoArg = args.Length > 1 ? args[1].ToLowerInvariant() : "toggle";
                bool? value = autoArg switch
                {
                    "on"  => true,
                    "off" => false,
                    _     => null   // toggle
                };
                syncService.SetAutoSync(value);
                var onOff = syncService.ShouldAutoSync ? "on" : "off";
                ui.ShowOsd($"sync: auto {onOff}", 2000);
                break;

            case "help":
                try
                {
                    var dlg = new Terminal.Gui.Dialog("Sync Help", 80, 28);
                    var tv = new Terminal.Gui.TextView { ReadOnly = true, WordWrap = true, X = 0, Y = 0, Width = Terminal.Gui.Dim.Fill(), Height = Terminal.Gui.Dim.Fill() };
                    tv.Text = StuiPodcast.App.HelpCatalog.SyncDoc;
                    dlg.Add(tv);
                    var ok = new Terminal.Gui.Button("OK", is_default: true);
                    ok.Clicked += () => Terminal.Gui.Application.RequestStop();
                    dlg.AddButton(ok);
                    Terminal.Gui.Application.Run(dlg);
                }
                catch { }
                break;

            case "":
                // :sync with no args = full sync
                ui.ShowOsd("sync: syncing…", 1500);
                _ = Task.Run(async () =>
                {
                    var (ok, msg) = await syncService.SyncAsync();
                    Application.MainLoop?.Invoke(() => ui.ShowOsd($"sync: {msg}", ok ? 2000 : 3000));
                });
                break;

            default:
                ui.ShowOsd($"sync: unknown sub-command '{sub}'", 2000);
                break;
        }
    }
}
