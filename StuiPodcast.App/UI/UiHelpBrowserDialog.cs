using System;
using System.Linq;
using System.Collections.Generic;
using Terminal.Gui;
// Falls HelpCatalog im Namespace StuiPodcast.App liegt, ist kein extra using nötig.
// Wenn dein HelpCatalog-Enum/Typen in Core liegen, lass das using unten stehen.
using StuiPodcast.Core;

namespace StuiPodcast.App.UI
{
    internal static class UiHelpBrowserDialog
    {
        #region public api
        public static void Show()
        {
            #region create dialog + tabs
            var dlg = new Dialog("Help — Keys & Commands", 100, 32);
            var tabs = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            dlg.Add(tabs);
            #endregion

            #region keys tab ui
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
            #endregion

            #region commands tab ui 
            var cmdHost    = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            var cmdSearch  = new TextField("") { X = 1, Y = 0, Width = Dim.Fill(2) };

            var catLabels = new List<string> { "All", "Most used" };
            try { catLabels.AddRange(Enum.GetNames(typeof(HelpCategory))); } catch { /* optional */ }

            var catList = new ListView {
                X = 1, Y = 2,
                Width = 18,
                Height = Dim.Fill(1)
            };
            catList.SetSource(catLabels);

            var cmdList = new ListView {
                X = Pos.Right(catList) + 1, Y = 2,
                Width  = Dim.Percent(35) - 19,
                Height = Dim.Fill(1)
            };

            var cmdDetails = new TextView {
                X = Pos.Right(cmdList) + 2, Y = 2,
                Width = Dim.Fill(1), Height = Dim.Fill(1),
                ReadOnly = true, WordWrap = true
            };

            cmdHost.Add(
                new Label("Search:"){ X=1, Y=0 },
                cmdSearch,
                new Label("Category:"){ X=1, Y=1 },
                catList,
                cmdList,
                cmdDetails
            );
            var cmdsTab = new TabView.Tab("Commands", cmdHost);
            tabs.AddTab(cmdsTab, false);
            #endregion

            #region scrollbars
            WireListScrollbar(keyList);
            WireListScrollbar(catList);
            WireListScrollbar(cmdList);
            WireTextViewScrollbar(keyDetails);
            WireTextViewScrollbar(cmdDetails);
            #endregion

            #region data + filter state
            List<KeyHelp> keyData = HelpCatalog.Keys.ToList();
            List<KeyHelp> keyFiltered = keyData.ToList();

            List<CmdHelp> allCmds = HelpCatalog.Commands.ToList();
            List<CmdHelp> cmdFiltered = allCmds.ToList();

            static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

            static void EnsureSelectionVisible(ListView lv)
            {
                var count = lv.Source?.Count ?? 0;
                if (count <= 0) return;

                var sel = Clamp(lv.SelectedItem, 0, count - 1);
                var viewH = Math.Max(0, lv.Bounds.Height);
                var top = Clamp(lv.TopItem, 0, Math.Max(0, count - 1));

                if (sel < top) lv.TopItem = sel;
                else if (sel >= top + viewH) lv.TopItem = Math.Max(0, sel - Math.Max(1, viewH - 1));

                lv.SetNeedsDisplay();
            }
            #endregion

            #region keys filtering + details
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
            #endregion

            #region commands filtering + details
            HelpCategory? CurrentCategory()
            {
                var idx = Clamp(catList.SelectedItem, 0, catLabels.Count - 1);
                if (idx == 0) return null; // all
                if (idx == 1) return (HelpCategory)(-1); // most used sentinel
                var name = catLabels[idx];
                try { if (Enum.TryParse<HelpCategory>(name, out var cat)) return cat; } catch { }
                return null;
            }

            void RefreshCmdList()
            {
                var q = cmdSearch.Text?.ToString() ?? "";
                var cat = CurrentCategory();
                IEnumerable<CmdHelp> src;

                if (cat.HasValue && (int)cat.Value == -1)
                {
                    try { src = HelpCatalog.MostUsed(int.MaxValue); }
                    catch { src = allCmds; }
                }
                else src = allCmds;

                if (cat.HasValue && (int)cat.Value >= 0)
                {
                    src = src.Where(c => {
                        try { return c.Category != null && c.Category.Equals(cat.Value); }
                        catch { return true; }
                    });
                }

                if (!string.IsNullOrWhiteSpace(q))
                {
                    src = src.Where(c =>
                        (c.Command ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (c.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(c.Args) && c.Args!.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                        (c.Aliases != null && c.Aliases.Any(a => (a ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)))
                    );
                }

                if (cat.HasValue && (int)cat.Value == -1)
                {
                    try { src = src.OrderBy(c => c.Rank).ThenBy(c => c.Command, StringComparer.OrdinalIgnoreCase); }
                    catch { src = src.OrderBy(c => c.Command, StringComparer.OrdinalIgnoreCase); }
                }
                else src = src.OrderBy(c => c.Command, StringComparer.OrdinalIgnoreCase);

                cmdFiltered = src.ToList();
                cmdList.SetSource(cmdFiltered.Select(c => c.Command ?? "").ToList());
                cmdList.SelectedItem = 0;
                cmdList.TopItem = 0;
                EnsureSelectionVisible(cmdList);

                if (cmdFiltered.Count == 0) cmdDetails.Text = "";
            }
            #endregion

            #region search wiring
            keySearch.KeyPress += (View.KeyEventEventArgs _) =>
                Application.MainLoop.AddIdle(() => { RefreshKeyList(); return false; });
            cmdSearch.KeyPress += (View.KeyEventEventArgs _) =>
                Application.MainLoop.AddIdle(() => { RefreshCmdList(); return false; });

            keySearch.KeyPress += e => {
                if (e.KeyEvent.Key == Key.Esc) { keySearch.Text = ""; RefreshKeyList(); e.Handled = true; }
                else if (e.KeyEvent.Key == Key.Enter) { keyList.SetFocus(); e.Handled = true; }
            };
            cmdSearch.KeyPress += e => {
                if (e.KeyEvent.Key == Key.Esc) { cmdSearch.Text = ""; RefreshCmdList(); e.Handled = true; }
                else if (e.KeyEvent.Key == Key.Enter) { cmdList.SetFocus(); e.Handled = true; }
            };
            #endregion

            #region list selection -> details
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

                string catLine = "";
                try { if (item.Category != null) catLine = $"Category: {item.Category}\n"; } catch { }

                string aliases  = (item.Aliases is { Length: > 0 }) ? $"Aliases: {string.Join(", ", item.Aliases)}\n" : "";
                string args     = string.IsNullOrWhiteSpace(item.Args) ? "" : $"Args: {item.Args}\n";
                string examples = (item.Examples is { Length: > 0 }) ? $"Examples:\n  - {string.Join("\n  - ", item.Examples)}\n" : "";

                cmdDetails.Text = $"{item.Command}\n\n{item.Description}\n\n{catLine}{aliases}{args}{examples}".TrimEnd();
                EnsureSelectionVisible(cmdList);
            };
            #endregion

            #region focus ring + nav
            var focusOrder = new View[] { catList, cmdList, cmdDetails, cmdSearch };
            int IndexOf(View v) => Array.IndexOf(focusOrder, v);
            void FocusByIndex(int i)
            {
                if (i < 0) i = focusOrder.Length - 1;
                if (i >= focusOrder.Length) i = 0;
                focusOrder[i].SetFocus();
                if (focusOrder[i] is ListView lv) EnsureSelectionVisible(lv);
            }
            void FocusNext(View from) => FocusByIndex(IndexOf(from) + 1);
            void FocusPrev(View from) => FocusByIndex(IndexOf(from) - 1);

            catList.KeyPress += e =>
            {
                var key = e.KeyEvent.Key; var ch = e.KeyEvent.KeyValue;
                if (ch == 'j' || key == Key.CursorDown) { MoveList(catList, +1); e.Handled = true; return; }
                if (ch == 'k' || key == Key.CursorUp)   { MoveList(catList, -1); e.Handled = true; return; }
                if (ch == 'g' && (key & Key.ShiftMask) == 0) { GoTop(catList); e.Handled = true; return; }
                if (ch == 'G') { GoBottom(catList); e.Handled = true; return; }
                if (ch == 'l' || key == Key.CursorRight) { cmdList.SetFocus(); e.Handled = true; return; }
                if (ch == '/') { cmdSearch.SetFocus(); e.Handled = true; return; }
                if (key == Key.Tab) { FocusNext(catList); e.Handled = true; return; }
                if (key == Key.BackTab) { FocusPrev(catList); e.Handled = true; return; }
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
                if (ch == 'h' || key == Key.CursorLeft) { catList.SetFocus(); e.Handled = true; return; }
                if (ch == 'l' || key == Key.CursorRight) { cmdDetails.SetFocus(); e.Handled = true; return; }
                if (key == Key.Tab) { FocusNext(cmdList); e.Handled = true; return; }
                if (key == Key.BackTab) { FocusPrev(cmdList); e.Handled = true; return; }
            };

            cmdDetails.KeyPress += e =>
            {
                var key = e.KeyEvent.Key; var ch = e.KeyEvent.KeyValue;
                if (ch == 'h' || key == Key.CursorLeft) { cmdList.SetFocus(); e.Handled = true; return; }
                if (ch == 'H') { catList.SetFocus(); e.Handled = true; return; }
                if (ch == '/') { cmdSearch.SetFocus(); e.Handled = true; return; }
                if (key == Key.Tab) { FocusNext(cmdDetails); e.Handled = true; return; }
                if (key == Key.BackTab) { FocusPrev(cmdDetails); e.Handled = true; return; }
            };

            cmdSearch.KeyPress += e =>
            {
                if (e.KeyEvent.Key == Key.Tab) { FocusNext(cmdSearch); e.Handled = true; return; }
                if (e.KeyEvent.Key == Key.BackTab) { FocusPrev(cmdSearch); e.Handled = true; return; }
            };

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
            #endregion

            #region events + init
            catList.SelectedItemChanged += _ =>
            {
                RefreshCmdList();
                EnsureSelectionVisible(catList);
            };

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

            RefreshKeyList();
            catList.SelectedItem = 0;
            RefreshCmdList();
            Application.Run(dlg);
            #endregion
        }
        #endregion

        #region scrollbar helpers
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
            catch { /* best effort */ }
        }

        private static void WireTextViewScrollbar(TextView tv)
        {
            try
            {
                var vbar = new ScrollBarView(tv, true);

                vbar.ChangedPosition += () =>
                {
                    tv.TopRow = Math.Max(0, vbar.Position);
                    if (vbar.Position != tv.TopRow) vbar.Position = tv.TopRow;
                    tv.SetNeedsDisplay();
                };

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
                    var size = Math.Max(ContentLines(), tv.Frame.Height);
                    vbar.Size = size;
                    vbar.Position = Math.Max(0, Math.Min(tv.TopRow, Math.Max(0, size - 1)));
                    vbar.Refresh();
                }

                tv.DrawContent += _ => RefreshBar();
                try { tv.TextChanged += () => { RefreshBar(); tv.SetNeedsDisplay(); }; } catch { }
            }
            catch { /* best effort */ }
        }
        #endregion
    }
}
