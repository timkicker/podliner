using System;
using System.Runtime.InteropServices;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI
{
    // central access to tui glyphs (unicode or ascii fallback)
    // use this class instead of hard literals in ui renderers
    public static class UIGlyphSet
    {
        public enum Profile { Unicode, Ascii }

        // active profile (default via autodetect)
        public static Profile Current { get; private set; } = AutoDetect();

        // manual override
        public static void Use(Profile p) => Current = p;

        #region detection and heuristics

        // auto-detect profile:
        // - env PODLINER_GLYPHS can force "ascii" or "unicode"
        // - windows -> unicode
        // - TERM contains 'dumb' -> ascii
        public static Profile AutoDetect()
        {
            try
            {
                // opt-out via env
                var force = Environment.GetEnvironmentVariable("PODLINER_GLYPHS");
                if (!string.IsNullOrWhiteSpace(force))
                {
                    if (force.Equals("ascii", StringComparison.OrdinalIgnoreCase)) return Profile.Ascii;
                    if (force.Equals("unicode", StringComparison.OrdinalIgnoreCase)) return Profile.Unicode;
                }

                // windows -> unicode by default
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return Profile.Unicode;

                // very old/small terminals
                var term = Environment.GetEnvironmentVariable("TERM") ?? "";
                if (term.Contains("dumb", StringComparison.OrdinalIgnoreCase))
                    return Profile.Ascii;

                return Profile.Unicode;
            }
            catch { return Profile.Unicode; }
        }

        #endregion

        #region nowplaying / list prefix

        // prefix for list line: active vs normal
        public static string NowPrefix(bool isNow) => isNow
            ? (Current == Profile.Unicode ? "▶ " : "> ")
            : "  ";

        #endregion

        #region progress / played

        // progress marker for episode list:
        // - if played -> checkmark or 'v'
        // - else choose glyph by ratio (0..1)
        public static char ProgressGlyph(double ratio, bool played)
        {
            if (played) return Current == Profile.Unicode ? '✔' : 'v';

            ratio = Math.Clamp(ratio, 0.0, 1.0);
            if (Current == Profile.Unicode)
            {
                if (ratio <= 0.0) return '○';
                if (ratio < 0.25) return '◔';
                if (ratio < 0.50) return '◑';
                if (ratio < 0.75) return '◕';
                return '●';
            }
            else
            {
                if (ratio <= 0.0) return 'o';
                if (ratio < 0.25) return 'c';
                if (ratio < 0.50) return 'O';
                if (ratio < 0.75) return '0';
                return '@';
            }
        }

        #endregion

        #region badges (saved / download / queue / offline)

        public static char Saved  => Current == Profile.Unicode ? '★' : '*';

        // download state badge (small symbol for list/osd)
        public static string DownloadStateBadge(DownloadState s)
        {
            var uni = Current == Profile.Unicode;
            return s switch
            {
                DownloadState.Queued     => uni ? "⌵" : "~",
                DownloadState.Running    => uni ? "⇣" : "v",
                DownloadState.Verifying  => uni ? "≈" : "~",
                DownloadState.Done       => uni ? "⬇" : "v",
                DownloadState.Failed     => uni ? "!"  : "!",
                DownloadState.Canceled   => uni ? "×"  : "x",
                _                        => " "
            };
        }

        // simple downloaded marker (legacy/fallback)
        public static char DownloadedMark => Current == Profile.Unicode ? '⬇' : 'v';

        public static char Queue  => Current == Profile.Unicode ? '⧉' : '#';
        public static char Offline => Current == Profile.Unicode ? '∅' : 'o';

        #endregion

        #region composite helpers

        // build 4-char badge string: saved, dl, queue, offline
        public static string ComposeBadges(bool isSaved, DownloadState dlState, bool isQueued, bool showOffline)
        {
            var s = isSaved ? Saved : ' ';
            var d = dlState switch
            {
                DownloadState.None => ' ',
                DownloadState.Done => DownloadedMark,
                DownloadState.Failed => '!',
                DownloadState.Canceled => Current == Profile.Unicode ? '×' : 'x',
                DownloadState.Queued => Current == Profile.Unicode ? '⌵' : '~',
                DownloadState.Running => Current == Profile.Unicode ? '⇣' : 'v',
                DownloadState.Verifying => Current == Profile.Unicode ? '≈' : '~',
                _ => ' '
            };
            var q = isQueued ? Queue : ' ';
            var o = showOffline ? Offline : ' ';
            return $"{s}{d}{q}{o}";
        }

        #endregion

        #region misc / ui labels

        public static string VolumePercent(int v) => $"{v}%";

        // speed label: zero/negative -> disabled symbol
        public static string SpeedLabel(double s) => (s <= 0) ? (Current == Profile.Unicode ? "—×" : "x") : $"{s:0.0}×";

        public static string Separator => Current == Profile.Unicode ? "  │  " : " | ";

        #endregion

        #region durations

        // format duration in ms to h:mm:ss or mm:ss
        public static string FormatDuration(long ms)
        {
            if (ms <= 0) return "--:--";
            long total = ms / 1000;
            long h = total / 3600;
            long m = (total % 3600) / 60;
            long s = total % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        }

        #endregion
    }
}
