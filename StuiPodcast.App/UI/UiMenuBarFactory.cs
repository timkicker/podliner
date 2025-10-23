using System;
using System.Threading.Tasks;
using Terminal.Gui;

namespace StuiPodcast.App.UI;

internal static class UiMenuBarFactory
{
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

    public static MenuBar Build(Callbacks cb)
    {
        // Reentrancy-Guard für lange/async Aktionen (lokal für diese MenuBar)
        bool isBusy = false;

        // --- Helpers ---------------------------------------------------------
        MenuItem Cmd(string text, string help, string cmd) => new(text, help, () => cb.Command(cmd));
        MenuItem Act(string text, string help, Action a)   => new(text, help, a);

        // Sichere Ausführung einer langen/async Aktion:
        // - OSD (start/finish/fehler)
        // - Reentrancy-Guard (kein Doppelstart)
        void AttachRunner(MenuItem item, Func<Task> action, string? busyOsdText = null, string? doneOsdText = null)
        {
            void Start()
            {
                if (isBusy) return;
                isBusy = true;

                // optional Busy-OSD
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
                        // UI-Refresh freundlich anstoßen
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
        // ---------------------------------------------------------------------

        // --- Menüstruktur ----------------------------------------------------
        var addFeedItem    = Act("_Add Feed… (:add URL)", "Open command line with :add", cb.AddFeed);
        var refreshAllItem = new MenuItem("_Refresh All (:refresh)", "Refresh all feeds", null);
        var quitItem       = Act("_Quit (Q)", "Quit application", cb.Quit);

        // Runner an Refresh hängen (falls Callback existiert)
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
                Act("Toggle _Played (m)", "Mark/Unmark played", () => cb.Command(":toggle-played")),
            }),

            new MenuBarItem("_View", new[]
            {
                Act("Toggle _Player Position (Ctrl+P)", "Top/bottom audioPlayer bar", () => cb.Command(":audioPlayer toggle")),
                Act("Toggle _Theme (t)", "Switch base/menu accent", cb.ToggleTheme),
                Cmd("Filter: _Unplayed (u)", "", ":filter toggle"),
            }),

            new MenuBarItem("_Navigate", new[]
            {
                Act("Focus _Feeds (h)", "Move focus to feeds", cb.FocusFeeds),
                Act("Focus _Episodes (l)", "Move focus to episodes", cb.FocusEpisodes),
                Act("Open _Details (i)", "Switch to details tab", cb.OpenDetails),
                Act("_Back from Details (Esc)", "Return to episodes", cb.BackFromDetails),
                new MenuItem("-", "", null),
                Act("Next _Unplayed (J / Shift+j)", "Next unplayed", cb.JumpNextUnplayed),
                Act("Prev _Unplayed (K / Shift+k)", "Prev unplayed", cb.JumpPrevUnplayed),
                new MenuItem("-", "", null),
                Act("Open _Command Line (:)", "Open command box", cb.ShowCommand),
                Act("_Search (/)", "Open search box", cb.ShowSearch),
            }),

            new MenuBarItem("_Help", new[]
            {
                Act("_Keys & Commands (:h)", "Help browser", () => StuiPodcast.App.UI.UiHelpBrowserDialog.Show()),
                Act("_Logs (F12)", "Show logs overlay", () => cb.Command(":logs")),
                Act("_About", "", () => MessageBox.Query("About", "Podliner: TUI podcast audioPlayer", "OK")),
            }),
        });

        return menu;
    }
}
