using System;
using System.Threading.Tasks;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

internal static class UiMenuBarFactory
{
    #region api
    public sealed record Callbacks(
        Action<string> Command,
        Func<Task>? RefreshRequested,
        Action AddFeed,
        Action Quit,
        Action FocusFeeds,
        Action FocusEpisodes,
        Action OpenDetails,
        Action BackFromDetails,
        Action JumpNextUnplayed,
        Action JumpPrevUnplayed,
        Action ShowCommand,
        Action ShowSearch,
        Action ToggleTheme
    );
    #endregion

    #region build
    public static MenuBar Build(Callbacks cb)
    {
        // simple reentrancy guard for async actions
        bool isBusy = false;

        // small helpers
        MenuItem Cmd(string text, string help, string cmd) => new(text, help, () => cb.Command(cmd));
        MenuItem Act(string text, string help, Action a)   => new(text, help, a);

        // run long action once, show osd, refresh ui
        void AttachRunner(MenuItem item, Func<Task> action, string? busyOsdText = null, string? doneOsdText = null)
        {
            void Start()
            {
                if (isBusy) return;
                isBusy = true;

                if (!string.IsNullOrWhiteSpace(busyOsdText))
                    cb.Command($":osd {busyOsdText}");

                Task.Run(async () =>
                {
                    try
                    {
                        await (action?.Invoke() ?? Task.CompletedTask);
                        if (!string.IsNullOrWhiteSpace(doneOsdText))
                            cb.Command($":osd {doneOsdText}");
                    }
                    catch (Exception ex)
                    {
                        cb.Command($":osd Fehler: {Short(ex.Message)}");
                    }
                    finally
                    {
                        Application.MainLoop?.Invoke(() => Application.Top?.SetNeedsDisplay());
                        isBusy = false;
                    }
                });
            }

            item.Action = Start;
        }

        static string Short(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "Unbekannter Fehler";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= 72 ? s : s[..72] + "…";
        }

        var addFeedItem    = Act("_Add Feed… (:add URL)", "open :add", cb.AddFeed);
        var refreshAllItem = new MenuItem("_Refresh All (:refresh)", "refresh feeds", null);
        var quitItem       = Act("_Quit (Q)", "quit", cb.Quit);

        AttachRunner(
            refreshAllItem,
            async () =>
            {
                if (cb.RefreshRequested != null)
                    await cb.RefreshRequested();
            },
            busyOsdText: "refreshing",
            doneOsdText: "refreshed"
        );

        var menu = new MenuBar(new[]
        {
            new MenuBarItem("_File", new[]
            {
                addFeedItem,
                refreshAllItem,
                new MenuItem("-", "", null),
                quitItem,
            }),

            new MenuBarItem("_Feeds", new[]
            {
                Cmd("_All Episodes", "", ":feed all"),
                Cmd("_Saved ★",      "", ":feed saved"),
                Cmd("_Downloaded ⬇", "", ":feed downloaded"),
            }),

            new MenuBarItem("_Playback", new[]
            {
                Cmd("_Play/Pause (Space)", "", ":toggle"),
                new MenuItem("-", "", null),
                Cmd("Seek _-10s (←/h/H)", "", ":seek -10"),
                Cmd("Seek _+10s (→/l/L)", "", ":seek +10"),
                Cmd("Seek _-60s (H)",     "", ":seek -60"),
                Cmd("Seek _+60s (L)",     "", ":seek +60"),
                Cmd("Seek _Start (g)",    "", ":seek 0:00"),
                Cmd("Seek _End (G)",      "", ":seek 100%"),
                new MenuItem("-", "", null),
                Cmd("_Speed −0.1 ([)", "", ":speed -0.1"),
                Cmd("S_peed +0.1 (])", "", ":speed +0.1"),
                Cmd("Speed _1.0 (=/1)", "", ":speed 1.0"),
                Cmd("Speed _1.25 (2)",  "", ":speed 1.25"),
                Cmd("Speed _1.5 (3)",   "", ":speed 1.5"),
                new MenuItem("-", "", null),
                Cmd("_Volume −5 (-)", "", ":vol -5"),
                Cmd("Volu_me +5 (+)", "", ":vol +5"),
                new MenuItem("-", "", null),
                Cmd("_Toggle Download Flag (d)", "", ":dl toggle"),
                Act("Toggle _Played (m)", "mark played", () => cb.Command(":toggle-played")),
            }),

            new MenuBarItem("_View", new[]
            {
                Act("Toggle _Player Position (Ctrl+P)", "bar top/bottom", () => cb.Command(":audioPlayer toggle")),
                Act("Toggle _Theme (t)", "cycle theme", cb.ToggleTheme),
                Cmd("Filter: _Unplayed (u)", "", ":filter toggle"),
            }),

            new MenuBarItem("_Navigate", new[]
            {
                Act("Focus _Feeds (h)", "focus feeds", cb.FocusFeeds),
                Act("Focus _Episodes (l)", "focus episodes", cb.FocusEpisodes),
                Act("Open _Details (i)", "show details", cb.OpenDetails),
                Act("_Back from Details (Esc)", "back to list", cb.BackFromDetails),
                new MenuItem("-", "", null),
                Act("Next _Unplayed (J / Shift+j)", "next unplayed", cb.JumpNextUnplayed),
                Act("Prev _Unplayed (K / Shift+k)", "prev unplayed", cb.JumpPrevUnplayed),
                new MenuItem("-", "", null),
                Act("Open _Command Line (:)", "command box", cb.ShowCommand),
                Act("_Search (/)", "search box", cb.ShowSearch),
            }),

            new MenuBarItem("_Help", new[]
            {
                Act("_Keys & Commands (:h)", "help", () => UiHelpBrowserDialog.Show()),
                Act("_Logs (F12)", "logs overlay", () => cb.Command(":logs")),
                Act("_About", "", () => MessageBox.Query("About", "Podliner: TUI podcast player", "OK")),
            }),
        });

        return menu;
    }
    #endregion
}
