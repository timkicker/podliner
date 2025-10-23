namespace StuiPodcast.App.Command;

internal static class CmdParser
{
    public static CmdParsed Parse(string raw)
    {
        var tokens = Tokenize(raw.Trim());
        if (tokens.Length == 0) return new CmdParsed(raw, "", Array.Empty<string>(), TopCommand.Unknown);
        var (cmd, args) = SplitCmd(tokens);
        return new CmdParsed(raw, cmd, args, MapTop(cmd));
    }

    // --- tokenizer/mapper (aus dem Altcode)
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

    private static TopCommand MapTop(string cmd)
    {
        if (cmd.StartsWith(":engine", StringComparison.OrdinalIgnoreCase)) return TopCommand.Engine;
        if (cmd.StartsWith(":opml", StringComparison.OrdinalIgnoreCase)) return TopCommand.Opml;

        // Aliases
        if (cmd.Equals(":a", StringComparison.OrdinalIgnoreCase)) return TopCommand.AddFeed;
        if (cmd.Equals(":r", StringComparison.OrdinalIgnoreCase)) return TopCommand.Refresh;

        // Neue Commands
        if (cmd.StartsWith(":open", StringComparison.OrdinalIgnoreCase)) return TopCommand.Open;
        if (cmd.StartsWith(":copy", StringComparison.OrdinalIgnoreCase)) return TopCommand.Copy;

        cmd = cmd?.Trim() ?? "";
        if (cmd.Equals(":h", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":help", StringComparison.OrdinalIgnoreCase)) return TopCommand.Help;
        if (cmd.Equals(":q", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":quit", StringComparison.OrdinalIgnoreCase)) return TopCommand.Quit;
        if (cmd.Equals(":q!", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":quit!", StringComparison.OrdinalIgnoreCase)) return TopCommand.QuitBang;

        // Vim writes
        if (cmd.Equals(":w", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":write", StringComparison.OrdinalIgnoreCase)) return TopCommand.Write;
        if (cmd.Equals(":wq", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":x", StringComparison.OrdinalIgnoreCase)) return TopCommand.WriteQuit;
        if (cmd.Equals(":wq!", StringComparison.OrdinalIgnoreCase)) return TopCommand.WriteQuitBang;

        // Neu
        if (cmd.StartsWith(":search", StringComparison.OrdinalIgnoreCase)) return TopCommand.Search;
        if (cmd.Equals(":now", StringComparison.OrdinalIgnoreCase)) return TopCommand.Now;
        if (cmd.StartsWith(":jump", StringComparison.OrdinalIgnoreCase)) return TopCommand.Jump;
        if (cmd.StartsWith(":theme", StringComparison.OrdinalIgnoreCase)) return TopCommand.Theme;

        if (cmd.StartsWith(":logs", StringComparison.OrdinalIgnoreCase)) return TopCommand.Logs;
        if (cmd.StartsWith(":osd", StringComparison.OrdinalIgnoreCase)) return TopCommand.Osd;

        if (cmd.Equals(":toggle", StringComparison.OrdinalIgnoreCase)) return TopCommand.Toggle;
        if (cmd.StartsWith(":seek", StringComparison.OrdinalIgnoreCase)) return TopCommand.Seek;
        if (cmd.StartsWith(":vol", StringComparison.OrdinalIgnoreCase)) return TopCommand.Volume;
        if (cmd.StartsWith(":speed", StringComparison.OrdinalIgnoreCase)) return TopCommand.Speed;
        if (cmd.StartsWith(":replay", StringComparison.OrdinalIgnoreCase)) return TopCommand.Replay;

        if (cmd.Equals(":next", StringComparison.OrdinalIgnoreCase)) return TopCommand.Next;
        if (cmd.Equals(":prev", StringComparison.OrdinalIgnoreCase)) return TopCommand.Prev;
        if (cmd.Equals(":play-next", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlayNext;
        if (cmd.Equals(":play-prev", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlayPrev;

        if (cmd.StartsWith(":goto", StringComparison.OrdinalIgnoreCase)) return TopCommand.Goto;
        if (cmd.Equals(":zt", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":H", StringComparison.OrdinalIgnoreCase)) return TopCommand.VimTop;
        if (cmd.Equals(":zz", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":M", StringComparison.OrdinalIgnoreCase)) return TopCommand.VimMiddle;
        if (cmd.Equals(":zb", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":L", StringComparison.OrdinalIgnoreCase)) return TopCommand.VimBottom;
        if (cmd.Equals(":next-unplayed", StringComparison.OrdinalIgnoreCase)) return TopCommand.NextUnplayed;
        if (cmd.Equals(":prev-unplayed", StringComparison.OrdinalIgnoreCase)) return TopCommand.PrevUnplayed;

        if (cmd.StartsWith(":save", StringComparison.OrdinalIgnoreCase)) return TopCommand.Save;
        if (cmd.StartsWith(":sort", StringComparison.OrdinalIgnoreCase)) return TopCommand.Sort;
        if (cmd.StartsWith(":filter", StringComparison.OrdinalIgnoreCase)) return TopCommand.Filter;
        if (cmd.StartsWith(":audioPlayer", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlayerBar;

        if (cmd.StartsWith(":net", StringComparison.OrdinalIgnoreCase)) return TopCommand.Net;
        if (cmd.StartsWith(":play-source", StringComparison.OrdinalIgnoreCase)) return TopCommand.PlaySource;

        if (cmd.StartsWith(":add", StringComparison.OrdinalIgnoreCase)) return TopCommand.AddFeed;
        if (cmd.StartsWith(":refresh", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith(":update", StringComparison.OrdinalIgnoreCase)) return TopCommand.Refresh;
        if (cmd.Equals(":rm-feed", StringComparison.OrdinalIgnoreCase) || cmd.Equals(":remove-feed", StringComparison.OrdinalIgnoreCase)) return TopCommand.RemoveFeed;
        if (cmd.StartsWith(":feed", StringComparison.OrdinalIgnoreCase)) return TopCommand.Feed;

        if (cmd.StartsWith(":history", StringComparison.OrdinalIgnoreCase)) return TopCommand.History;

        return TopCommand.Unknown;
    }
}
