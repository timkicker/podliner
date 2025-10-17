using System;
using System.Linq;
using System.Collections.Generic;
using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI
{
    internal static class HelpBrowserDialog
    {
        public static void Show()
        {
            var dlg = new Dialog("Help — Keys & Commands", 100, 32);
            var tabs = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            dlg.Add(tabs);

            // ==== KEYS TAB ====
            var keysHost   = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            var keySearch  = new TextField("") { X = 1, Y = 0, Width = Dim.Fill(2) };
            var keyList    = new ListView {
                X = 1, Y = 2,
                Width  = Dim.Percent(35),
                Height = Dim.Fill(1)
            };
            var keyDetails = new TextView {
                X = Pos.Right(keyList) + 2, Y = 2,
                Width = Dim.Fill(1), Height = Dim.Fill(1),
                ReadOnly = true, WordWrap = true
            };
            keysHost.Add(new Label("Search:"){ X=1, Y=0 }, keySearch, keyList, keyDetails);
            var keysTab = new TabView.Tab("Keys", keysHost);
            tabs.AddTab(keysTab, true);

            // ==== COMMANDS TAB ====
            var cmdHost    = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            var cmdSearch  = new TextField("") { X = 1, Y = 0, Width = Dim.Fill(2) };
            var cmdList    = new ListView {
                X = 1, Y = 2,
                Width  = Dim.Percent(35),
                Height = Dim.Fill(1)
            };
            var cmdDetails = new TextView {
                X = Pos.Right(cmdList) + 2, Y = 2,
                Width = Dim.Fill(1), Height = Dim.Fill(1),
                ReadOnly = true, WordWrap = true
            };
            cmdHost.Add(new Label("Search:"){ X=1, Y=0 }, cmdSearch, cmdList, cmdDetails);
            var cmdsTab = new TabView.Tab("Commands", cmdHost);
            tabs.AddTab(cmdsTab, false);

            // ==== Scrollbars verdrahten ====
            WireListScrollbar(keyList);
            WireListScrollbar(cmdList);
            WireTextViewScrollbar(keyDetails);
            WireTextViewScrollbar(cmdDetails);

            // ==== Daten/Filter ====
            List<KeyHelp> keyData = HelpCatalog.Keys.ToList();
            List<CmdHelp> cmdData = HelpCatalog.Commands.ToList();
            List<KeyHelp> keyFiltered = keyData.ToList();
            List<CmdHelp> cmdFiltered = cmdData.ToList();

            static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

            // Sichtbarkeit der Auswahl erzwingen (manuelle Scroll-Logik)
            static void EnsureSelectionVisible(ListView lv)
            {
                var count = lv.Source?.Count ?? 0;
                if (count <= 0) return;

                var sel = Clamp(lv.SelectedItem, 0, count - 1);
                var viewH = Math.Max(0, lv.Bounds.Height);         // sichtbare Zeilen
                var top = Clamp(lv.TopItem, 0, Math.Max(0, count - 1));

                if (sel < top)
                    lv.TopItem = sel;
                else if (sel >= top + viewH)
                    lv.TopItem = Math.Max(0, sel - Math.Max(1, viewH - 1));

                lv.SetNeedsDisplay();
            }

            void RefreshKeyList()
            {
                var q = keySearch.Text?.ToString() ?? "";
                keyFiltered = string.IsNullOrWhiteSpace(q)
                    ? keyData
                    : keyData.Where(k =>
                        (k.Key ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (k.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(k.Notes) && k.Notes!.Contains(q, StringComparison.OrdinalIgnoreCase))
                      ).ToList();

                keyList.SetSource(keyFiltered.Select(k => k.Key ?? "").ToList());
                keyList.SelectedItem = 0;
                keyList.TopItem = 0;
                EnsureSelectionVisible(keyList);
            }

            void RefreshCmdList()
            {
                var q = cmdSearch.Text?.ToString() ?? "";
                cmdFiltered = string.IsNullOrWhiteSpace(q)
                    ? cmdData
                    : cmdData.Where(c =>
                        (c.Command ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (c.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(c.Args) && c.Args!.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                        (c.Aliases != null && c.Aliases.Any(a => (a ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)))
                      ).ToList();

                cmdList.SetSource(cmdFiltered.Select(c => c.Command ?? "").ToList());
                cmdList.SelectedItem = 0;
                cmdList.TopItem = 0;
                EnsureSelectionVisible(cmdList);
            }

            // Suche tippen -> filtern
            keySearch.KeyPress += (View.KeyEventEventArgs _) =>
                Application.MainLoop.AddIdle(() => { RefreshKeyList(); return false; });
            cmdSearch.KeyPress += (View.KeyEventEventArgs _) =>
                Application.MainLoop.AddIdle(() => { RefreshCmdList(); return false; });

            // Esc löscht, Enter fokussiert Liste
            keySearch.KeyPress += e => {
                if (e.KeyEvent.Key == Key.Esc) { keySearch.Text = ""; RefreshKeyList(); e.Handled = true; }
                else if (e.KeyEvent.Key == Key.Enter) { keyList.SetFocus(); e.Handled = true; }
            };
            cmdSearch.KeyPress += e => {
                if (e.KeyEvent.Key == Key.Esc) { cmdSearch.Text = ""; RefreshCmdList(); e.Handled = true; }
                else if (e.KeyEvent.Key == Key.Enter) { cmdList.SetFocus(); e.Handled = true; }
            };

            // Detail-Update (aus gefiltertem View)
            keyList.SelectedItemChanged += _ =>
            {
                if (keyFiltered.Count == 0) { keyDetails.Text = ""; return; }
                var idx = Clamp(keyList.SelectedItem, 0, keyFiltered.Count - 1);
                var item = keyFiltered[idx];
                var notes = string.IsNullOrWhiteSpace(item.Notes) ? "" : $"\n\nNotes: {item.Notes}";
                keyDetails.Text = $"{item.Key}\n\n{item.Description}{notes}";
                EnsureSelectionVisible(keyList);
            };
            cmdList.SelectedItemChanged += _ =>
            {
                if (cmdFiltered.Count == 0) { cmdDetails.Text = ""; return; }
                var idx = Clamp(cmdList.SelectedItem, 0, cmdFiltered.Count - 1);
                var item = cmdFiltered[idx];
                string aliases  = (item.Aliases is { Length: > 0 }) ? $"Aliases: {string.Join(", ", item.Aliases)}\n" : "";
                string args     = string.IsNullOrWhiteSpace(item.Args) ? "" : $"Args: {item.Args}\n";
                string examples = (item.Examples is { Length: > 0 }) ? $"Examples:\n  - {string.Join("\n  - ", item.Examples)}\n" : "";
                cmdDetails.Text = $"{item.Command}\n\n{item.Description}\n\n{aliases}{args}{examples}".TrimEnd();
                EnsureSelectionVisible(cmdList);
            };

            // Navigation (hjkl / Pfeile) – nach jedem Move Sichtbarkeit erzwingen
            void MoveList(ListView lv, int d)
            {
                var c = lv.Source?.Count ?? 0;
                if (c <= 0) return;
                lv.SelectedItem = Clamp(lv.SelectedItem + d, 0, c - 1);
                EnsureSelectionVisible(lv);
            }
            void GoTop(ListView lv)    { var c = lv.Source?.Count ?? 0; if (c > 0) { lv.SelectedItem = 0; EnsureSelectionVisible(lv); } }
            void GoBottom(ListView lv) { var c = lv.Source?.Count ?? 0; if (c > 0) { lv.SelectedItem = c - 1; EnsureSelectionVisible(lv); } }
            void FocusKeysTab() { tabs.SelectedTab = keysTab; keyList.SetFocus(); EnsureSelectionVisible(keyList); }
            void FocusCmdsTab() { tabs.SelectedTab = cmdsTab; cmdList.SetFocus(); EnsureSelectionVisible(cmdList); }

            keyList.KeyPress += e =>
            {
                if (keySearch.HasFocus) return;
                var key = e.KeyEvent.Key; var ch = e.KeyEvent.KeyValue;
                if (ch == 'j' || key == Key.CursorDown) { MoveList(keyList, +1); e.Handled = true; return; }
                if (ch == 'k' || key == Key.CursorUp)   { MoveList(keyList, -1); e.Handled = true; return; }
                if (ch == 'g' && (key & Key.ShiftMask) == 0) { GoTop(keyList); e.Handled = true; return; }
                if (ch == 'G') { GoBottom(keyList); e.Handled = true; return; }
                if (ch == '/') { keySearch.SetFocus(); e.Handled = true; return; }
                if (ch == 'h') { FocusKeysTab(); e.Handled = true; return; }
                if (ch == 'l') { FocusCmdsTab(); e.Handled = true; return; }
            };

            cmdList.KeyPress += e =>
            {
                if (cmdSearch.HasFocus) return;
                var key = e.KeyEvent.Key; var ch = e.KeyEvent.KeyValue;
                if (ch == 'j' || key == Key.CursorDown) { MoveList(cmdList, +1); e.Handled = true; return; }
                if (ch == 'k' || key == Key.CursorUp)   { MoveList(cmdList, -1); e.Handled = true; return; }
                if (ch == 'g' && (key & Key.ShiftMask) == 0) { GoTop(cmdList); e.Handled = true; return; }
                if (ch == 'G') { GoBottom(cmdList); e.Handled = true; return; }
                if (ch == '/') { cmdSearch.SetFocus(); e.Handled = true; return; }
                if (ch == 'h') { FocusKeysTab(); e.Handled = true; return; }
                if (ch == 'l') { FocusCmdsTab(); e.Handled = true; return; }
            };

            // Global close / tab-nav
            dlg.KeyPress += e =>
            {
                var ch = e.KeyEvent.KeyValue;
                if (ch == 'h') { FocusKeysTab(); e.Handled = true; return; }
                if (ch == 'l') { FocusCmdsTab(); e.Handled = true; return; }
                if (ch == 'q' || ch == 'Q' || e.KeyEvent.Key == Key.Esc || e.KeyEvent.Key == Key.F12)
                {
                    Application.RequestStop();
                    e.Handled = true;
                }
            };

            // initial
            RefreshKeyList();
            RefreshCmdList();
            Application.Run(dlg);
        }

        // ---------- Scrollbar-Helfer ----------
        private static void WireListScrollbar(ListView lv)
        {
            try
            {
                var vbar = new ScrollBarView(lv, true);
                vbar.ChangedPosition += () =>
                {
                    lv.TopItem = Math.Max(0, Math.Min(vbar.Position, Math.Max(0, (lv.Source?.Count ?? 0) - 1)));
                    if (vbar.Position != lv.TopItem) vbar.Position = lv.TopItem;
                    lv.SetNeedsDisplay();
                };

                lv.DrawContent += _ =>
                {
                    var size = lv.Source?.Count ?? 0;
                    vbar.Size = Math.Max(0, size);
                    vbar.Position = Math.Max(0, Math.Min(lv.TopItem, Math.Max(0, size - 1)));
                    vbar.Refresh();
                };
            }
            catch { /* best effort, falls TUI-Version abweicht */ }
        }

        private static void WireTextViewScrollbar(TextView tv)
        {
            // Falls deine Terminal.Gui-Version eingebaute Scrollbars hat, kannst du alternativ das hier versuchen:
            // try { tv.VerticalScrollBarVisible = true; tv.HorizontalScrollBarVisible = false; return; } catch {}

            try
            {
                var vbar = new ScrollBarView(tv, true);

                vbar.ChangedPosition += () =>
                {
                    tv.TopRow = System.Math.Max(0, vbar.Position);
                    if (vbar.Position != tv.TopRow) vbar.Position = tv.TopRow;
                    tv.SetNeedsDisplay();
                };

                // Zähle Zeilen ohne Wrap (robust und versionsunabhängig)
                int ContentLines()
                {
                    var s = tv.Text?.ToString() ?? string.Empty;
                    if (s.Length == 0) return 0;
                    int lines = 1;
                    for (int i = 0; i < s.Length; i++)
                        if (s[i] == '\n') lines++;
                    return lines;
                }

                void RefreshBar()
                {
                    // Mindestgröße = Frame.Height, damit die Leiste immer konsistent wirkt
                    var size = System.Math.Max(ContentLines(), tv.Frame.Height);
                    vbar.Size = size;
                    vbar.Position = System.Math.Max(0, System.Math.Min(tv.TopRow, System.Math.Max(0, size - 1)));
                    vbar.Refresh();
                }

                // Bei jedem Repaint aktualisieren
                tv.DrawContent += _ => RefreshBar();

                // Wenn verfügbar, auf Textänderungen reagieren (nicht in allen Versionen vorhanden)
                try { tv.TextChanged += () => { RefreshBar(); tv.SetNeedsDisplay(); }; } catch { /* best effort */ }
            }
            catch { /* best effort */ }
        }

    }
}
