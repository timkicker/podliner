using System;
using System.Runtime.InteropServices;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI
{
    /// <summary>
    /// Zentraler Zugriff auf alle TUI-Glyphen (Unicode oder ASCII-Fallback).
    /// Verwende diese Klasse statt harter Literale in UI-Renderern.
    /// </summary>
    public static class GlyphSet
    {
        public enum Profile { Unicode, Ascii }

        /// <summary>Aktives Profil (Default via AutoDetect()).</summary>
        public static Profile Current { get; private set; } = AutoDetect();

        /// <summary>Manueller Override (z.B. per :theme native → Ascii).</summary>
        public static void Use(Profile p) => Current = p;

        /// <summary>Automatische Erkennung: Windows nach VT-Enable → Unicode, sonst Unicode; falls TERM sehr alt → ASCII.</summary>
        public static Profile AutoDetect()
        {
            try
            {
                // Opt-out per Env
                var force = Environment.GetEnvironmentVariable("PODLINER_GLYPHS");
                if (!string.IsNullOrWhiteSpace(force))
                {
                    if (force.Equals("ascii", StringComparison.OrdinalIgnoreCase)) return Profile.Ascii;
                    if (force.Equals("unicode", StringComparison.OrdinalIgnoreCase)) return Profile.Unicode;
                }

                // Minimalheuristik: Windows → nach unserem VT-Enable i.d.R. Unicode ok.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return Profile.Unicode;

                // Sehr alte Terminals?
                var term = Environment.GetEnvironmentVariable("TERM") ?? "";
                if (term.Contains("dumb", StringComparison.OrdinalIgnoreCase))
                    return Profile.Ascii;

                return Profile.Unicode;
            }
            catch { return Profile.Unicode; }
        }

        // ---------- AudioPlayer / NowPlaying ----------

        /// <summary>Prefix für Listenzeile: aktiv vs. normal.</summary>
        public static string NowPrefix(bool isNow) => isNow
            ? (Current == Profile.Unicode ? "▶ " : "> ")
            : "  ";

        // ---------- Progress / Played ----------

        /// <summary>
        /// Fortschritts-Marker für Episodenliste:
        /// played=true → ✔ / 'v'
        /// sonst je nach ratio (0..1): ○ ◔ ◑ ◕ ●  bzw. o c O 0 @ (ASCII).
        /// </summary>
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

        // ---------- Badges (Saved / Download / Queue / Offline) ----------

        public static char Saved  => Current == Profile.Unicode ? '★' : '*';

        /// <summary>Download-Status: kleines Symbol für Episodenliste/OSD.</summary>
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

        /// <summary>Einfacher "ist heruntergeladen" Badge (Legacy/Fallback).</summary>
        public static char DownloadedMark => Current == Profile.Unicode ? '⬇' : 'v';

        public static char Queue  => Current == Profile.Unicode ? '⧉' : '#';
        public static char Offline => Current == Profile.Unicode ? '∅' : 'o';

        // ---------- Composite Helpers ----------

        /// <summary>
        /// Baut die 4-Badge-Leiste "saved, dl, queue, offline" als 4 Zeichen (oder Leerzeichen).
        /// </summary>
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

        // ---------- Volume / Misc (optional) ----------

        public static string VolumePercent(int v) => $"{v}%";

        public static string SpeedLabel(double s) => (s <= 0) ? (Current == Profile.Unicode ? "—×" : "x") : $"{s:0.0}×";

        public static string Separator => Current == Profile.Unicode ? "  │  " : " | ";

        // ---------- Durations ----------

        public static string FormatDuration(long ms)
        {
            if (ms <= 0) return "--:--";
            long total = ms / 1000;
            long h = total / 3600;
            long m = (total % 3600) / 60;
            long s = total % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        }
    }
}
