using System.Collections.Generic;

namespace StuiPodcast.App
{
    public record KeyHelp(string Key, string Description, string? Notes = null);

    public record CmdHelp(
        string Command,                 // canonical command, e.g. ":refresh"
        string Description,             // one-line description
        string? Args = null,            // brief arg syntax, e.g. "[+/-N | NN% | mm:ss]"
        string[]? Aliases = null,       // e.g. new[]{":h",":help"}
        string[]? Examples = null       // e.g. new[]{":seek +10",":vol 80"}
    );

    public static class HelpCatalog
    {
        // --- Keyboard (non-commands) ---
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
            new("J / K", "Play next / previous unplayed"),
            new("⇧J / ⇧K ","  Move item down / up (only in ⧉ Queue)"),

            new("m", "Toggle played/unplayed flag"),
            new("u", "Toggle unplayed filter"),
            new("d", "Toggle ⬇ downloaded flag on episode"),

            new(":", "Command mode"),
            new("/", "Search (Enter to apply, n to repeat)"),
            new("t", "Toggle theme"),
            new("F12", "Open logs overlay"),
            new("q", "Quit"),
        };

        // --- Commands (":" commands) ---
        public static readonly List<CmdHelp> Commands = new()
        {
            new(":add", "Add a new podcast feed by RSS/Atom URL.",
                "<rss-url>",
                Examples: new[]{ ":add https://example.com/feed.xml" }),

            new(":refresh", "Refresh all feeds." ,
                Aliases: new[]{":update"}),

            new(":q", "Quit application.", Aliases: new[]{":quit"}),

            new(":h", "Show this help.", Aliases: new[]{":help"}),

            new(":logs", "Show logs overlay (tail).",
                "[N]",
                Examples: new[]{ ":logs", ":logs 1000" }),

            new(":seek", "Seek in current episode.",
                "[+/-N sec | NN% | mm:ss]",
                Examples: new[]{ ":seek +10", ":seek 80%", ":seek 12:34" }),

            new(":vol", "Set or change volume.",
                "[N | +/-N]  (0–100)",
                Examples: new[]{ ":vol 70", ":vol +5", ":vol -10" }),

            new(":speed", "Set or change playback speed.",
                "[S | +/-D]  (0.25–3.0)",
                Examples: new[]{ ":speed 1.0", ":speed +0.1", ":speed -0.25" }),

            new(":save", "Toggle or set 'saved' (★) for selected episode.",
                "[on|off|true|false|+|-]",
                Examples: new[]{ ":save", ":save on", ":save off" }),

            new(":dl", "Toggle or set 'downloaded' (⬇) for selected episode.",
                "[on|off|true|false|+|-]",
                Aliases: new[]{":download"},
                Examples: new[]{ ":dl", ":dl on", ":download off" }),

            new(":player", "Place the player bar.",
                "[top|bottom|toggle]",
                Examples: new[]{ ":player toggle", ":player top" }),

            new(":filter", "Set or toggle unplayed filter.",
                "[unplayed|all|toggle]",
                Examples: new[]{ ":filter toggle", ":filter unplayed" }),

            new(":sort", "Sort the episode list.",
                "show | reset | reverse | by <pubdate|title|played|progress|feed> [asc|desc]",
                Examples: new[]{ ":sort show", ":sort by title asc", ":sort reverse" }),

            new(":next", "Select next item (no auto-play)."),
            new(":prev", "Select previous item (no auto-play)."),
            new(":play-next", "Play next item."),
            new(":play-prev", "Play previous item."),

            new(":next-unplayed", "Play next unplayed episode."),
            new(":prev-unplayed", "Play previous unplayed episode."),

            new(":replay", "Replay from 0:00 or jump back N seconds.",
                "[N]",
                Examples: new[]{ ":replay", ":replay 30" }),
            
            new(":history", "History actions (view-only feed)",
                "clear | size <n>",
                Examples: new[]{ ":history clear", ":history size 300" }),



            new(":goto", "Select absolute list position.",
                "top|start|bottom|end",
                Examples: new[]{ ":goto top", ":goto end" }),

            new(":zt / :zz / :zb", "Vim-style list positioning (top/center/bottom).",
                Aliases: new[]{":H",":M",":L"}),

            new(":rm-feed", "Remove the currently selected feed.",
                Aliases: new[]{":remove-feed", ":feed remove"}),
        };
    }
}
