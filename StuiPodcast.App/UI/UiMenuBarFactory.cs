using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Terminal.Gui;
using StuiPodcast.App; // for HelpCatalog & enums

namespace StuiPodcast.App.UI
{

    internal static class UiMenuBarFactory
    {
        #region API

        public sealed record Callbacks(
            Action<string> Command,
            Func<Task>?    RefreshRequested,
            Action         AddFeed,
            Action         Quit,
            Action         FocusFeeds,
            Action         FocusEpisodes,
            Action         OpenDetails,
            Action         BackFromDetails,
            Action         JumpNextUnplayed,
            Action         JumpPrevUnplayed,
            Action         ShowCommand,        // ":" (empty)
            Action<string> ShowCommandSeeded,  // ":" with seed, e.g. ":seek "
            Action         ToggleTheme
        );

        #endregion

        #region Build
        
        static string Seed(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return ":";
            cmd = cmd.Trim();
            if (!cmd.StartsWith(":")) cmd = ":" + cmd;
            return cmd + (cmd.EndsWith(" ") ? "" : " ");
        }


        public static MenuBar Build(Callbacks cb)
        {
            // reentrancy guard for async menu actions
            bool isBusy = false;

            // tiny helpers
            MenuItem Cmd(string text, string help, string cmd) => new(text, help, () => cb.Command(cmd));
            MenuItem Act(string text, string help, Action a)   => new(text, help, a);

            // run a potentially long action once; show lightweight OSD info
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
                            cb.Command($":osd Fehler: {TrimOneLine(ex.Message)}");
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

            static string TrimOneLine(string? s)
            {
                if (string.IsNullOrEmpty(s)) return "Unbekannter Fehler";
                s = s.Replace('\n', ' ').Replace('\r', ' ');
                return s.Length <= 72 ? s : s[..72] + "…";
            }


            MenuItem[] BuildFromCatalog(HelpCategory cat)
            {
                var items = HelpCatalog.Commands
                    .Where(c => c.Category == cat)
                    .OrderBy(c => c.Rank)
                    .ThenBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
                    .Select(c =>
                    {
                        // label: ":seek [args] — description"
                        var label = c.Args is { Length: > 0 }
                            ? $"{c.Command} {c.Args} — {c.Description}"
                            : $"{c.Command} — {c.Description}";

                        Action action =
                            c.Args is { Length: > 0 }
                                ? () => cb.ShowCommandSeeded(Seed(c.Command))
                                : () => cb.Command(c.Command);

                        var mi = new MenuItem(label, "", action);

                        // Special case: :refresh runs via async runner
                        if (string.Equals(c.Command, ":refresh", StringComparison.OrdinalIgnoreCase))
                        {
                            AttachRunner(mi,
                                async () => { if (cb.RefreshRequested != null) await cb.RefreshRequested(); },
                                busyOsdText: "refreshing",
                                doneOsdText: "refreshed");
                        }

                        return mi;
                    })
                    .ToArray();

                return items;
            }

            // Quick access entries that should remain visible at the top of their menus
            var quickFeeds = new[]
            {
                Cmd("_All Episodes", "", ":feed all"),
                Cmd("_Saved ★",      "", ":feed saved"),
                Cmd("_Downloaded ⬇", "", ":feed downloaded"),
                new MenuItem("-", "", null),
            };

            var quickPlayback = new[]
            {
                Cmd("_Play/Pause (Space)", "", ":toggle"),
                new MenuItem("-", "", null),
                Cmd("Seek _-10s (←/h/H)", "", ":seek -10"),
                Cmd("Seek _+10s (→/l/L)", "", ":seek +10"),
                Cmd("Seek _Start (g)",    "", ":seek 0:00"),
                Cmd("Seek _End (G)",      "", ":seek 100%"),
                new MenuItem("-", "", null),
            };

            var quickView = new[]
            {
                Act("Toggle _Player Position (Ctrl+P)", "bar top/bottom", () => cb.Command(":audioPlayer toggle")),
                Act("Toggle _Theme (t)", "cycle theme", cb.ToggleTheme),
                Cmd("Filter: _Unplayed (u)", "", ":filter toggle"),
                new MenuItem("-", "", null),
            };

            var quickNavigate = new[]
            {
                Act("Focus _Feeds (h)",         "focus feeds",          cb.FocusFeeds),
                Act("Focus _Episodes (l)",      "focus episodes",       cb.FocusEpisodes),
                Act("Open _Details (i)",        "show details",         cb.OpenDetails),
                Act("_Back from Details (Esc)", "back to list",         cb.BackFromDetails),
                new MenuItem("-", "", null),
                Act("Next _Unplayed (J)",       "next unplayed",        cb.JumpNextUnplayed),
                Act("Prev _Unplayed (K)",       "prev unplayed",        cb.JumpPrevUnplayed),
                new MenuItem("-", "", null),
                Act("Open _Command Line (:)",   "command box",          cb.ShowCommand),
                // search is kept in Shell's keybinds; no menu item required here
                new MenuItem("-", "", null),
            };

            // Catalog-driven groups
            var feeds       = BuildFromCatalog(HelpCategory.Feeds);
            var playback    = BuildFromCatalog(HelpCategory.Playback);
            var viewSort    = BuildFromCatalog(HelpCategory.PlayerTheme)
                              .Concat(BuildFromCatalog(HelpCategory.SortFilter)).ToArray();
            var navigate    = BuildFromCatalog(HelpCategory.Navigation);
            var downloads   = BuildFromCatalog(HelpCategory.Downloads);

            // Newly placed groups:
            var queue       = BuildFromCatalog(HelpCategory.Queue);       // will go under _Navigate
            var opml        = BuildFromCatalog(HelpCategory.OPML);        // will go under _File

            var miscTop     = HelpCatalog.MostUsed(6)
                               .Select(c => new MenuItem(
                                   c.Args is { Length: > 0 }
                                       ? $"{c.Command} {c.Args} — {c.Description}"
                                       : $"{c.Command} — {c.Description}",
                                   "",
                                   c.Args is { Length: > 0 }
                                       ? () => cb.ShowCommandSeeded($"{c.Command} ")
                                       : () => cb.Command(c.Command)
                               ))
                               .ToArray();

            // File menu: partially manual (better UX labels), now includes OPML section
            var addFeedItem    = Act("_Add Feed… (:add URL)",  "open :add", cb.AddFeed);
            var refreshAllItem = new MenuItem("_Refresh All (:refresh)", "refresh feeds", null);
            AttachRunner(
                refreshAllItem,
                async () => { if (cb.RefreshRequested != null) await cb.RefreshRequested(); },
                busyOsdText: "refreshing",
                doneOsdText: "refreshed"
            );
            var quitItem       = Act("_Quit (Q)", "quit", cb.Quit);

            var fileItems = new List<MenuItem>
            {
                addFeedItem,
                refreshAllItem
            };

            if (opml.Length > 0)
            {
                fileItems.Add(new MenuItem("-", "", null));
                fileItems.Add(new MenuItem("OPML", "", null) { CanExecute = () => false });
                fileItems.AddRange(opml);
            }

            fileItems.Add(new MenuItem("-", "", null));
            fileItems.Add(quitItem);

            // Navigate menu: append Queue section
            var navigateItems = new List<MenuItem>();
            navigateItems.AddRange(quickNavigate);
            navigateItems.AddRange(navigate);
            if (queue.Length > 0)
            {
                navigateItems.Add(new MenuItem("-", "", null));
                navigateItems.Add(new MenuItem("— Queue —", "", null) { CanExecute = () => false });
                navigateItems.AddRange(queue);
            }

            // Help menu without spread operator
            var helpItems =
                new[]
                {
                    new MenuItem("— Most used —", "", null) { CanExecute = () => false }
                }
                .Concat(miscTop)
                .Concat(new[]
                {
                    new MenuItem("-", "", null),
                    new MenuItem("_Keys & Commands (:h)", "help", () => cb.Command(":help")),
                    new MenuItem("_Logs (F12)", "logs overlay", () => cb.Command(":logs")),
                    new MenuItem("_About", "", () => MessageBox.Query("About", "Podliner: TUI podcast player", "OK")),
                })
                .ToArray();

            var menu = new MenuBar(new[]
            {
                new MenuBarItem("_File",      fileItems.ToArray()),
                new MenuBarItem("_Feeds",     quickFeeds.Concat(feeds).ToArray()),
                new MenuBarItem("_Playback",  quickPlayback.Concat(playback).ToArray()),
                new MenuBarItem("_View",      quickView.Concat(viewSort).ToArray()),
                new MenuBarItem("_Navigate",  navigateItems.ToArray()),
                new MenuBarItem("_Downloads", downloads),
                new MenuBarItem("_Network",   BuildFromCatalog(HelpCategory.NetworkEngine)),
                new MenuBarItem("_Help",      helpItems),
            });

            return menu;
        }

        #endregion
    }
}
