using System.Collections.Generic;

namespace StuiPodcast.App
{
    public record KeyHelp(string Key, string Description, string? Notes = null);

    public record CmdHelp(
        string Command,
        string Description,
        string? Args = null,
        string[]? Aliases = null,
        string[]? Examples = null
    );

    public static class HelpCatalog
    {
        public static readonly List<KeyHelp> Keys = new()
        {
            new("Space", "Toggle play/pause"),
            new("← / →", "Seek -10s / +10s"),
            new("H / L", "Seek -60s / +60s"),
            new("g / G", "Jump to start / end"),
            new("- / +", "Volume down / up"),
            new("[ / ]", "Slower / faster"),
            new("= or 1", "Reset speed to 1.0×"),
            new("2 / 3", "Speed presets 1.25× / 1.5×"),

            new("j / k", "Move selection down / up"),
            new("h / l", "Focus feeds / episodes"),
            new("Enter", "Play selected episode"),
            new("i", "Open Shownotes tab"),
            new("Esc (in Shownotes)", "Back to Episodes"),
            new("J / K", "Next / Prev unplayed"),
            new("⇧J / ⇧K", "Move item down / up (Queue)"),

            new("m", "Toggle played flag"),
            new("u", "Toggle unplayed filter"),
            new("d", "Toggle ⬇ download flag"),

            new(":", "Command mode"),
            new("/", "Search (Enter to apply, n to repeat)"),
            new("t", "Toggle theme"),
            new("F12", "Logs overlay"),
            new("q", "Quit"),
        };

        public static readonly List<CmdHelp> Commands = new()
        {
            new(":add", "Add a new podcast feed by RSS/Atom URL.",
                "<rss-url>",
                Aliases: new[]{ ":a" },
                Examples: new[]{ ":add https://example.com/feed.xml", ":a https://example.com/feed.xml" }),

            new(":refresh", "Refresh all feeds.",
                Aliases: new[]{ ":update", ":r" },
                Examples: new[]{ ":refresh", ":r" }),

            new(":quit", "Quit application.",
                Aliases: new[]{ ":q" },
                Examples: new[]{ ":quit", ":q" }),

            new(":help", "Show this help.",
                Aliases: new[]{ ":h" },
                Examples: new[]{ ":help", ":h" }),

            new(":logs", "Show logs overlay (tail).",
                "[N]", Examples: new[]{ ":logs", ":logs 1000" }),

            new(":seek", "Seek in current episode.",
                "[+/-N sec | NN% | mm:ss]",
                Examples: new[]{ ":seek +10", ":seek 80%", ":seek 12:34" }),

            new(":vol", "Set or change volume.",
                "[N | +/-N]  (0–100)",
                Examples: new[]{ ":vol 70", ":vol +5", ":vol -10" }),

            new(":speed", "Set or change speed.",
                "[S | +/-D]  (0.25–3.0)",
                Examples: new[]{ ":speed 1.0", ":speed +0.1", ":speed -0.25" }),

            new(":save", "Toggle or set 'saved' (★) for selected episode.",
                "[on|off|true|false|+|-]"),

            // Primär: Langform, Kurzform nur als Alias
            new(":download", "Mark/Unmark for download (auto-queued).",
                "[start|cancel]",
                Aliases: new[]{ ":dl" },
                Examples: new[]{ ":download", ":download start", ":dl", ":dl cancel" }),

            new(":downloads", "Downloads overview & actions.",
                "[retry-failed]",
                Examples: new[]{ ":downloads", ":downloads retry-failed" }),

            new(":open", "Open episode website or audio in system default.",
                "[site|audio]",
                Examples: new[]{ ":open", ":open site", ":open audio" }),

            new(":copy", "Copy episode info to clipboard (fallback: OSD).",
                "url|title|guid",
                Examples: new[]{ ":copy", ":copy url", ":copy title", ":copy guid" }),

            new(":player", "Place the player bar.",
                "[top|bottom|toggle]"),

            new(":filter", "Set or toggle unplayed filter.",
                "[unplayed|all|toggle]"),

            new(":sort", "Sort the episode list.",
                "show | reset | reverse | by <pubdate|title|played|progress|feed> [asc|desc]"),

            new(":next", "Select next item (no auto-play)."),
            new(":prev", "Select previous item (no auto-play)."),
            new(":play-next", "Play next item."),
            new(":play-prev", "Play previous item."),

            new(":next-unplayed", "Play next unplayed."),
            new(":prev-unplayed", "Play previous unplayed."),

            new(":replay", "Replay from 0:00 or jump back N seconds.",
                "[N]"),

            new(":history", "History actions (view-only feed)",
                "clear | size <n>"),

            new(":goto", "Select absolute list position.",
                "top|start|bottom|end"),

            // Diese drei haben keine „Langform“ – daher bleiben sie primär in Kurzform.
            new(":zt / :zz / :zb", "Vim-style list positioning (top/center/bottom).",
                Aliases: new[]{":H",":M",":L"}),

            new(":remove-feed", "Remove the currently selected feed.",
                Aliases: new[]{ ":rm-feed", ":feed remove" },
                Examples: new[]{ ":remove-feed", ":rm-feed" }),

            // Playback engine
            new(":engine", "Select or inspect playback engine.",
                "[show|help|auto|vlc|mpv|ffplay|diag]",
                Examples: new[]{ ":engine", ":engine mpv", ":engine help", ":engine diag" }),

            // OPML (Import/Export)
            new(":opml", "Import or export OPML (feed migration).",
                "import <path> [--update-titles] | export [<path>]",
                Examples: new[]{
                    ":opml import ~/feeds.opml",
                    ":opml import feeds.opml --update-titles",
                    ":opml export",
                    ":opml export ~/stui-feeds.opml"
                }),
        };

        public static readonly string EngineDoc =
@"Playback engines & capabilities

• VLC (libVLC) — default
  - Supports: seek, pause, volume, speed, local files, HTTP
  - Recommended. Mature & feature-complete.

• MPV (mpv, IPC)
  - Supports: seek, pause, volume, speed, local files, HTTP
  - Requires 'mpv' in PATH. Uses IPC socket. Very capable.

• FFplay (ffplay, limited)
  - Supports: play/stop; *coarse seek* by restart (-ss). Speed/volume only at start.
  - Live pause/seek/speed/volume not supported.
  - Intended as last-resort fallback.

Switching engines
  :engine           → show current engine & capabilities
  :engine help      → show this guide
  :engine auto      → prefer VLC → MPV → FFplay
  :engine vlc|mpv|ffplay → set preference
  :engine diag      → show active engine name, capabilities, preference & last-used

Notes
  - On FFplay, ':seek' restarts playback from the new position ('coarse seek').
  - If an action isn't supported by the active engine, you'll see a short OSD hint.
  - Linux: install packages 'vlc', 'mpv', 'ffmpeg'.
  - macOS: brew install vlc mpv ffmpeg
  - Windows: install VLC/MPV/FFmpeg and ensure they are in PATH.";

        // Optional: kurzer Leitfaden für OPML (falls du ihn im UI anzeigen willst)
        public static readonly string OpmlDoc =
@"OPML import/export (feed migration)

Import
  :opml import <path> [--update-titles]
    - Reads OPML 2.0 and shows a summary: new / duplicates / invalid.
    - Default policy:
        • Groups are ignored (flat import)
        • Existing feed titles are NOT overwritten
        • No online validation (works offline)

Export
  :opml export [<path>]
    - Writes a flat OPML (UTF-8) with all current feeds.
    - If <path> is omitted, a sensible default is used (Documents/stui-feeds.opml).

Examples
  :opml import ~/feeds.opml
  :opml import feeds.opml --update-titles
  :opml export
  :opml export ~/stui-feeds.opml";
    }
}
