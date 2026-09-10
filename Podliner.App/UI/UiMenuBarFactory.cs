using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Terminal.Gui;
using Podliner.App;

namespace Podliner.App.UI
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
            Action         ShowCommand,        
            Action<string> ShowCommandSeeded,  
            Action         ToggleTheme
        );

        #endregion

        #region Build
        
        // internal so the seeding rule can be tested directly.
        // Terminal.Gui draws a null child as a separator rule, but MenuItem[] is
    // not annotated nullable, so each separator would need its own
    // suppression. One helper keeps that in a single place.
    private static MenuItem Separator => null!;

    internal static string Seed(string cmd)
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
                Cmd("All Episodes", "", ":feed all"),
                Cmd("Saved ★",      "", ":feed saved"),
                Cmd("Downloaded ⬇", "", ":feed downloaded"),
                Separator,
            };

            var quickPlayback = new[]
            {
                Cmd("Play/Pause (Space)", "", ":toggle"),
                Separator,
                Cmd("Seek -10s (←/h/H)", "", ":seek -10"),
                Cmd("Seek +10s (→/l/L)", "", ":seek +10"),
                Cmd("Seek Start (g)",    "", ":seek 0:00"),
                Cmd("Seek End (G)",      "", ":seek 100%"),
                Separator,
            };

            var quickView = new[]
            {
                Act("Toggle Player Position (Ctrl+P)", "bar top/bottom", () => cb.Command(":audioPlayer toggle")),
                Act("Toggle Theme (t)", "cycle theme", cb.ToggleTheme),
                Cmd("Filter: Unplayed (u)", "", ":filter toggle"),
                Separator,
            };

            var quickNavigate = new[]
            {
                Act("Focus Feeds (h)",         "focus feeds",          cb.FocusFeeds),
                Act("Focus Episodes (l)",      "focus episodes",       cb.FocusEpisodes),
                Act("Open Details (i)",        "show details",         cb.OpenDetails),
                Act("Back from Details (Esc)", "back to list",         cb.BackFromDetails),
                Separator,
                Act("Next Unplayed (J)",       "next unplayed",        cb.JumpNextUnplayed),
                Act("Prev Unplayed (K)",       "prev unplayed",        cb.JumpPrevUnplayed),
                Separator,
                Act("Open Command Line (:)",   "command box",          cb.ShowCommand),
                // search is kept in Shell's keybinds; no menu item required here
                Separator,
            };

            // Catalog-driven groups
            var feeds       = BuildFromCatalog(HelpCategory.Feeds);
            var playback    = BuildFromCatalog(HelpCategory.Playback);
            var viewSort    = BuildFromCatalog(HelpCategory.PlayerTheme)
                              .Concat(BuildFromCatalog(HelpCategory.SortFilter)).ToArray();
            var navigate    = BuildFromCatalog(HelpCategory.Navigation);
            var downloads   = BuildFromCatalog(HelpCategory.Downloads);

            // Newly placed groups:
            var queue       = BuildFromCatalog(HelpCategory.Queue);       // will go under Navigate
            var opml        = BuildFromCatalog(HelpCategory.OPML);        // will go under File

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
            var addFeedItem    = Act("Add Feed… (:add URL)",  "open :add", cb.AddFeed);
            var refreshAllItem = new MenuItem("Refresh All (:refresh)", "refresh feeds", null);
            AttachRunner(
                refreshAllItem,
                async () => { if (cb.RefreshRequested != null) await cb.RefreshRequested(); },
                busyOsdText: "refreshing",
                doneOsdText: "refreshed"
            );
            var quitItem       = Act("Quit (Q)", "quit", cb.Quit);

            var fileItems = new List<MenuItem>
            {
                addFeedItem,
                refreshAllItem
            };

            if (opml.Length > 0)
            {
                fileItems.Add(Separator);
                fileItems.Add(new MenuItem("OPML", "", null) { CanExecute = () => false });
                fileItems.AddRange(opml);
            }

            fileItems.Add(Separator);
            fileItems.Add(quitItem);

            // Navigate menu: append Queue section
            var navigateItems = new List<MenuItem>();
            navigateItems.AddRange(quickNavigate);
            navigateItems.AddRange(navigate);
            if (queue.Length > 0)
            {
                navigateItems.Add(Separator);
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
                    Separator,
                    new MenuItem("Keys & Commands (:h)", "help", () => cb.Command(":help")),
                    new MenuItem("Logs (F12)", "logs overlay", () => cb.Command(":logs")),
                    new MenuItem("About", "", () => MessageBox.Query("About", "Podliner: TUI podcast player", "OK")),
                })
                .ToArray();

            var menu = new MenuBar(new[]
            {
                new MenuBarItem("File",      fileItems.ToArray()),
                new MenuBarItem("Feeds",     quickFeeds.Concat(feeds).ToArray()),
                new MenuBarItem("Playback",  quickPlayback.Concat(playback).ToArray()),
                new MenuBarItem("View",      quickView.Concat(viewSort).ToArray()),
                new MenuBarItem("Navigate",  navigateItems.ToArray()),
                new MenuBarItem("Downloads", downloads),
                new MenuBarItem("Network",   BuildFromCatalog(HelpCategory.NetworkEngine)),
                new MenuBarItem("Help",      helpItems),
            });

            return menu;
        }

        #endregion
    }
}
