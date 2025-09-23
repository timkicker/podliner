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

            // --- KEYS TAB -------------------------------------------------------
            var keysHost   = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            var keySearch  = new TextField("") { X = 1, Y = 0, Width = Dim.Fill(2) };
            var keyList    = new ListView    { X = 1, Y = 2, Width = 36, Height = Dim.Fill(1) };
            var keyDetails = new TextView    { X = Pos.Right(keyList) + 2, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1), ReadOnly = true, WordWrap = true };

            keysHost.Add(new Label("Search:") { X = 1, Y = 0 }, keySearch, keyList, keyDetails);
            var keysTab = new TabView.Tab("Keys", keysHost);
            tabs.AddTab(keysTab, true);

            // --- COMMANDS TAB ---------------------------------------------------
            var cmdHost    = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            var cmdSearch  = new TextField("") { X = 1, Y = 0, Width = Dim.Fill(2) };
            var cmdList    = new ListView    { X = 1, Y = 2, Width = 36, Height = Dim.Fill(1) };
            var cmdDetails = new TextView    { X = Pos.Right(cmdList) + 2, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1), ReadOnly = true, WordWrap = true };

            cmdHost.Add(new Label("Search:") { X = 1, Y = 0 }, cmdSearch, cmdList, cmdDetails);
            var cmdsTab = new TabView.Tab("Commands", cmdHost);
            tabs.AddTab(cmdsTab, false);

            // --- Daten ----------------------------------------------------------
            List<KeyHelp> keyData = HelpCatalog.Keys.ToList();
            List<CmdHelp> cmdData = HelpCatalog.Commands.ToList();

            // --- Helpers ---------------------------------------------------------
            static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
            void MoveList(ListView lv, int d)
            {
                var c = lv.Source?.Count ?? 0;
                if (c <= 0) return;
                lv.SelectedItem = Clamp(lv.SelectedItem + d, 0, c - 1);
            }
            void GoTop(ListView lv)    { var c = lv.Source?.Count ?? 0; if (c > 0) lv.SelectedItem = 0; }
            void GoBottom(ListView lv) { var c = lv.Source?.Count ?? 0; if (c > 0) lv.SelectedItem = c - 1; }
            void FocusKeysTab() { tabs.SelectedTab = keysTab; keyList.SetFocus(); }
            void FocusCmdsTab() { tabs.SelectedTab = cmdsTab; cmdList.SetFocus(); }

            void RefreshKeyList()
            {
                var q = keySearch.Text?.ToString() ?? "";
                var filtered = string.IsNullOrWhiteSpace(q)
                    ? keyData
                    : keyData.Where(k =>
                           (k.Key ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                           (k.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrWhiteSpace(k.Notes) && k.Notes!.Contains(q, StringComparison.OrdinalIgnoreCase)))
                      .ToList();

                keyList.SetSource(filtered.Select(k => k.Key ?? "").ToList());
                keyList.SelectedItem = 0;
            }

            void RefreshCmdList()
            {
                var q = cmdSearch.Text?.ToString() ?? "";
                var filtered = string.IsNullOrWhiteSpace(q)
                    ? cmdData
                    : cmdData.Where(c =>
                           (c.Command ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                           (c.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrWhiteSpace(c.Args) && c.Args!.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                           (c.Aliases != null && c.Aliases.Any(a => (a ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))))
                      .ToList();

                cmdList.SetSource(filtered.Select(c => c.Command ?? "").ToList());
                cmdList.SelectedItem = 0;
            }

            keySearch.KeyPress += (View.KeyEventEventArgs _) =>
                Application.MainLoop.AddIdle(() => { RefreshKeyList(); return false; });
            cmdSearch.KeyPress += (View.KeyEventEventArgs _) =>
                Application.MainLoop.AddIdle(() => { RefreshCmdList(); return false; });

            keyList.SelectedItemChanged += _ =>
            {
                var src = keyList.Source?.ToList() ?? new List<object>();
                var idx = Clamp(keyList.SelectedItem, 0, Math.Max(0, src.Count - 1));
                if (src.Count == 0) { keyDetails.Text = ""; return; }

                var name  = src[idx]?.ToString() ?? "";
                var item  = HelpCatalog.Keys.FirstOrDefault(k => (k.Key ?? "") == name) ?? keyData.First();
                var notes = string.IsNullOrWhiteSpace(item.Notes) ? "" : $"\n\nNotes: {item.Notes}";
                keyDetails.Text = $"{item.Key}\n\n{item.Description}{notes}";
            };

            cmdList.SelectedItemChanged += _ =>
            {
                var src = cmdList.Source?.ToList() ?? new List<object>();
                var idx = Clamp(cmdList.SelectedItem, 0, Math.Max(0, src.Count - 1));
                if (src.Count == 0) { cmdDetails.Text = ""; return; }

                var name = src[idx]?.ToString() ?? "";
                var item = HelpCatalog.Commands.FirstOrDefault(c => (c.Command ?? "") == name) ?? cmdData.First();

                string aliases  = (item.Aliases is { Length: > 0 }) ? $"Aliases: {string.Join(", ", item.Aliases)}\n" : "";
                string args     = string.IsNullOrWhiteSpace(item.Args) ? "" : $"Args: {item.Args}\n";
                string examples = (item.Examples is { Length: > 0 }) ? $"Examples:\n  - {string.Join("\n  - ", item.Examples)}\n" : "";

                cmdDetails.Text = $"{item.Command}\n\n{item.Description}\n\n{aliases}{args}{examples}".TrimEnd();
            };

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

            // Global close + tab nav
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

            // Initial fill
            RefreshKeyList();
            RefreshCmdList();
            Application.Run(dlg);
        }
    }
}
