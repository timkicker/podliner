using Serilog;
using Terminal.Gui;
using StuiPodcast.Core;
using StuiPodcast.App.Debug;
using StuiPodcast.App.Services;
using Attribute = Terminal.Gui.Attribute;
using StuiPodcast.App.UI.Controls;

namespace StuiPodcast.App.UI;

// UiShell: colour schemes, player placement and the download badge.
public sealed partial class UiShell
{
    public void SetPlayerPlacement(bool atTop)
    {
        UI(() =>
        {
            if (_player == null || _mainWin == null) return;

            _playerAtTop = atTop;

            if (_playerAtTop)
            {
                _player.X = 1;
                _player.Y = 1;
                _mainWin.Y = 1 + UiPlayerPanel.PlayerFrameH;
                _mainWin.Height = Dim.Fill();
            }
            else
            {
                _mainWin.Y = 1;
                _mainWin.Height = Dim.Fill(UiPlayerPanel.PlayerFrameH);
                _player.X = 1;
                _player.Y = Pos.Bottom(_mainWin);
            }

            RequestRepaint();
        });
    }

    public void SetDownloadBadge(string? text)
    {
        try
        {
            Terminal.Gui.Application.MainLoop?.Invoke(() =>
            {
                if (_dlBadge == null) return;

                var show = !string.IsNullOrWhiteSpace(text);
                _dlBadge.Visible = show;
                _dlBadge.Text = show ? text : string.Empty;

                _dlBadge.SetNeedsDisplay();
                Application.Top?.SetNeedsDisplay();
            });
        }
        catch { }
    }

    public void TogglePlayerPlacement() => SetPlayerPlacement(!_playerAtTop);
    public void ShowDetails(Episode e) => UI(() => _episodesPane?.ShowDetails(e));

    // Called from SelectionChanged. Resets the chapters tab state so the
    // previous episode's list can't stay on screen. Only re-loads if the
    // Chapters tab is currently active — otherwise we wait for activation.
    void MaybeResetChaptersForSelection(Episode? ep)
    {
        if (_episodesPane == null) return;
        _chaptersActiveEpisodeId = ep?.Id;

        if (_episodesPane.Tabs.SelectedTab == _episodesPane.ChaptersTab)
        {
            if (ep == null)
            {
                _episodesPane.ChaptersList.ShowPlaceholder("Select an episode to see chapters");
                _episodesPane.SetChaptersTabCount(null);
                return;
            }
            _episodesPane.ChaptersList.ShowPlaceholder("Loading chapters…");
            _episodesPane.SetChaptersTabCount(null);
            try { ChaptersLoadRequested?.Invoke(ep); }
            catch (Exception ex) { Log.Debug(ex, "chapters-load-requested subscriber threw"); }
        }
        else
        {
            // Tab not visible — clear the panel so a future activation
            // starts fresh instead of showing the old episode's chapters.
            _episodesPane.ChaptersList.ShowPlaceholder(ep == null
                ? "Select an episode to see chapters"
                : "Open Chapters tab to load");
            _episodesPane.SetChaptersTabCount(null);
        }
    }



    private void ApplyTheme()
    {
        UI(() =>
        {
            if (_theme == ThemeMode.User)
            {
                var u = UiShellColorSchemes.BuildUserSchemes();

                if (Application.Top != null)          Application.Top.ColorScheme = u.Main;
                if (_mainWin != null)                 _mainWin.ColorScheme = u.Main;
                if (_rightRoot != null)               _rightRoot.ColorScheme = u.Main;

                if (_menu != null)                    _menu.ColorScheme = u.Menu;
                if (_dlBadge != null)                 _dlBadge.ColorScheme = u.Menu;

                if (_feedsPane?.Frame != null)        _feedsPane.Frame.ColorScheme = u.Main;
                if (_feedsPane?.List  != null)        _feedsPane.List.ColorScheme  = u.List;

                if (_episodesPane?.Tabs != null)      _episodesPane.Tabs.ColorScheme = u.Main;
                if (_episodesPane?.List != null)      _episodesPane.List.ColorScheme = u.List;

                if (_player != null)
                {
                    _player.ColorScheme          = u.Main;
                    _player.Progress.ColorScheme = new ColorScheme {
                        Normal    = u.Status.Normal,
                        Focus     = u.Status.Focus,
                        HotNormal = u.Menu.HotNormal,
                        HotFocus  = u.Menu.HotFocus,
                        Disabled  = u.Status.Disabled
                    };
                    _player.VolBar.ColorScheme   = _player.Progress.ColorScheme;
                }

                if (_commandBox != null) _commandBox.ColorScheme = u.Input;
                if (_searchBox  != null) _searchBox.ColorScheme  = u.Input;

                if (_episodesPane?.Details != null)
                    _episodesPane.Details.ColorScheme = UiShellColorSchemes.BuildDetailsScheme();

                _osd.ApplyTheme();
                RequestRepaint();
                return;
            }

            ColorScheme scheme = _theme switch
            {
                ThemeMode.MenuAccent => Colors.Menu,
                ThemeMode.Base       => Colors.Base,
                ThemeMode.Native     => BuildNativeScheme(),
                _ => Colors.Base
            };

            if (Application.Top != null) Application.Top.ColorScheme = scheme;

            if (_mainWin != null)                 _mainWin.ColorScheme = scheme;
            if (_feedsPane?.Frame != null)        _feedsPane.Frame.ColorScheme = scheme;
            if (_rightRoot != null)               _rightRoot.ColorScheme = scheme;
            if (_player != null)                  _player.ColorScheme = scheme;
            if (_feedsPane?.List != null)         _feedsPane.List.ColorScheme = scheme;
            if (_episodesPane?.Tabs != null)      _episodesPane.Tabs.ColorScheme = scheme;

            if (_player != null)
            {
                _player.Progress.ColorScheme = MakeProgressScheme();
                _player.VolBar.ColorScheme   = MakeProgressScheme();
            }

            _osd.ApplyTheme();
            if (_commandBox != null) _commandBox.ColorScheme = scheme;
            if (_searchBox  != null) _searchBox.ColorScheme  = scheme;

            RequestRepaint();
        });
    }

    private static ColorScheme BuildNativeScheme()
    {
        return new ColorScheme
        {
            Normal    = Colors.Base.Normal,
            Focus     = Colors.Base.Normal,
            HotNormal = Colors.Base.Normal,
            HotFocus  = Colors.Base.Normal,
            Disabled  = Colors.Base.Disabled
        };
    }

    private ColorScheme MakeProgressScheme()
    {
        if (_theme == ThemeMode.Native)
        {
            return new ColorScheme
            {
                Normal    = Colors.Base.Normal,
                Focus     = Colors.Base.Normal,
                HotNormal = Colors.Base.Normal,
                HotFocus  = Colors.Base.Normal,
                Disabled  = Colors.Base.Disabled
            };
        }
        return new ColorScheme
        {
            Normal    = Colors.Base.Normal,
            Focus     = Colors.Base.Focus,
            Disabled  = Colors.Base.Disabled,
            HotNormal = Colors.Menu.HotNormal,
            HotFocus  = Colors.Menu.HotFocus
        };
    }
}
