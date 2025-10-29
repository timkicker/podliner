
namespace StuiPodcast.App.Command
{
    internal static class CmdParser
    {
        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        // alias to canonical 
        private static readonly Dictionary<string, string> Canon = new(Ci)
        {
            [":h"] = ":help",
            [":q"] = ":quit",
            [":q!"] = ":quit!",
            [":w"] = ":write",
            [":wq"] = ":wq", 
            [":x"] = ":wq",
            [":a"] = ":add",
            [":r"] = ":refresh",
            [":rm-feed"] = ":remove-feed",
        };

        // exact matches
        private static readonly Dictionary<string, TopCommand> Exact = new(Ci)
        {
            [":help"] = TopCommand.Help,
            [":quit"] = TopCommand.Quit,
            [":quit!"] = TopCommand.QuitBang,

            [":write"] = TopCommand.Write,
            [":wq"]    = TopCommand.WriteQuit,

            [":toggle"]    = TopCommand.Toggle,
            [":next"]      = TopCommand.Next,
            [":prev"]      = TopCommand.Prev,
            [":play-next"] = TopCommand.PlayNext,
            [":play-prev"] = TopCommand.PlayPrev,
            [":now"]       = TopCommand.Now,

            [":zt"] = TopCommand.VimTop,    [":H"] = TopCommand.VimTop,
            [":zz"] = TopCommand.VimMiddle, [":M"] = TopCommand.VimMiddle,
            [":zb"] = TopCommand.VimBottom, [":L"] = TopCommand.VimBottom,

            [":add"]         = TopCommand.AddFeed,
            [":refresh"]     = TopCommand.Refresh,
            [":remove-feed"] = TopCommand.RemoveFeed,
        };

        // perfix rules
        private static readonly (string Prefix, TopCommand Cmd)[] Prefixes =
        [
            (":engine",       TopCommand.Engine),
            (":opml",         TopCommand.Opml),
            (":open",         TopCommand.Open),
            (":copy",         TopCommand.Copy),

            (":search",       TopCommand.Search),
            (":jump",         TopCommand.Jump),
            (":theme",        TopCommand.Theme),
            (":logs",         TopCommand.Logs),
            (":osd",          TopCommand.Osd),

            (":seek",         TopCommand.Seek),
            (":vol",          TopCommand.Volume),
            (":speed",        TopCommand.Speed),
            (":replay",       TopCommand.Replay),

            (":goto",         TopCommand.Goto),
            (":next-unplayed",TopCommand.NextUnplayed),
            (":prev-unplayed",TopCommand.PrevUnplayed),

            (":save",         TopCommand.Save),
            (":sort",         TopCommand.Sort),
            (":filter",       TopCommand.Filter),
            (":audioplayer",  TopCommand.PlayerBar), 

            (":net",          TopCommand.Net),
            (":play-source",  TopCommand.PlaySource),

            (":feed",         TopCommand.Feed),
            (":history",      TopCommand.History),
            (":update",       TopCommand.Refresh)
        ];

        #region Public API

        public static CmdParsed Parse(string raw)
        {
            var tokens = Tokenize(raw.Trim());
            if (tokens.Length == 0)
                return new CmdParsed(raw, "", Array.Empty<string>(), TopCommand.Unknown);

            var (cmd0, args) = SplitCmd(tokens);

            var cmd = Canonicalize(cmd0);

            var top = MapTop(cmd);

            return new CmdParsed(raw, cmd, args, top);
        }

        #endregion

        #region Tokenize/Split

        private static string[] Tokenize(string raw)
        {
            var list = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inQuotes = false;
            char quoteChar = '\0';

            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];

                if (ch == '\\' && i + 1 < raw.Length) { i++; cur.Append(raw[i]); continue; }

                if (!inQuotes && (ch == '"' || ch == '\''))
                { inQuotes = true; quoteChar = ch; continue; }

                if (inQuotes && ch == quoteChar)
                { inQuotes = false; quoteChar = '\0'; continue; }

                if (!inQuotes && char.IsWhiteSpace(ch))
                { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } continue; }

                cur.Append(ch);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list.ToArray();
        }

        private static (string cmd, string[] args) SplitCmd(string[] tokens)
        {
            if (tokens.Length == 0) return ("", Array.Empty<string>());
            var cmd = tokens[0];
            var args = tokens.Skip(1).ToArray();
            return (cmd, args);
        }

        #endregion

        #region Mapping / Helpers

        private static string Canonicalize(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return "";
            if (cmd[0] != ':') cmd = ":" + cmd;
            return Canon.TryGetValue(cmd, out var longForm) ? longForm : cmd;
        }

        private static TopCommand MapTop(string cmd)
        {
            if (cmd.Length == 0) return TopCommand.Unknown;

            if (Exact.TryGetValue(cmd, out var mapped))
                return mapped;

            foreach (var (pref, top) in Prefixes)
                if (cmd.StartsWith(pref, StringComparison.OrdinalIgnoreCase))
                    return top;

            return TopCommand.Unknown;
        }

        #endregion
    }
}
