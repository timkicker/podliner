using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace StuiPodcast.App.UI;

// Pure colour-scheme factory for the "User" theme. Kept as a static helper
// (no UiShell instance state) so the palette values + scheme construction
// live apart from the widget-mutation logic in UiShell.ApplyTheme.
//
// Hex → Color mapping goes through ApproxAnsi: Terminal.Gui renders via the
// 16-colour terminal palette, so we pick the closest named ANSI colour
// rather than trying to emit true-colour escapes that won't render on many
// terminals.
internal static class UiShellColorSchemes
{
    public sealed class UserSchemes
    {
        public ColorScheme Main  = new();
        public ColorScheme Menu  = new();
        public ColorScheme List  = new();
        public ColorScheme Input = new();
        public ColorScheme Dialog = new();
        public ColorScheme Status = new();
    }

    private sealed record Palette(string Bg, string Bg2, string Dim, string Fg, string Comment,
        string Orange, string Green, string Pink, string Red, string Cyan,
        string Purple, string Blue, string Yellow);

    private static readonly Palette UserPal = new(
        Bg:     "#2a2a2a",
        Bg2:    "#333333",
        Dim:    "#222222",
        Fg:     "#bec1bf",
        Comment:"#8a8a8a",
        Orange: "#df970d",
        Green:  "#6aaa64",
        Pink:   "#b16286",
        Red:    "#a14040",
        Cyan:   "#64aaaa",
        Purple: "#9762b1",
        Blue:   "#6289b1",
        Yellow: "#d5a442"
    );

    // Scheme for the read-only Details pane: a dim backdrop (Bg2) without
    // focus differentiation so the user doesn't see cursor movement between
    // focused/unfocused states while reading.
    public static ColorScheme BuildDetailsScheme()
    {
        var p = UserPal;
        var normal = Attr(C(p.Fg), C(p.Bg2));
        var hot    = Attr(C(p.Orange), C(p.Bg2));
        return new ColorScheme
        {
            Normal    = normal,
            Focus     = normal,
            HotNormal = hot,
            HotFocus  = hot,
            Disabled  = Attr(C(p.Comment), C(p.Bg2))
        };
    }

    public static UserSchemes BuildUserSchemes()
    {
        var p = UserPal;

        var main = new ColorScheme {
            Normal    = Attr(C(p.Fg),     C(p.Bg)),
            Focus     = Attr(Color.Black,  C(p.Orange)),
            HotNormal = Attr(C(p.Orange),  C(p.Bg)),
            HotFocus  = Attr(Color.Black,  C(p.Orange)),
            Disabled  = Attr(C(p.Comment), C(p.Bg))
        };

        var menu = new ColorScheme {
            Normal    = Attr(C(p.Fg),     C(p.Bg)),
            Focus     = Attr(Color.Black,  C(p.Orange)),
            HotNormal = Attr(C(p.Orange),  C(p.Bg)),
            HotFocus  = Attr(Color.Black,  C(p.Orange)),
            Disabled  = Attr(C(p.Comment), C(p.Bg))
        };

        var list = new ColorScheme {
            Normal    = Attr(C(p.Fg),     C(p.Bg)),
            Focus     = Attr(Color.Black,  C(p.Pink)),
            HotNormal = Attr(C(p.Orange),  C(p.Bg)),
            HotFocus  = Attr(Color.Black,  C(p.Pink)),
            Disabled  = Attr(C(p.Comment), C(p.Bg))
        };

        var input = new ColorScheme {
            Normal    = Attr(C(p.Fg),     C(p.Dim)),
            Focus     = Attr(Color.Black,  C(p.Green)),
            HotNormal = Attr(C(p.Orange),  C(p.Dim)),
            HotFocus  = Attr(Color.Black,  C(p.Green)),
            Disabled  = Attr(C(p.Comment), C(p.Dim))
        };

        var dialog = new ColorScheme {
            Normal    = Attr(C(p.Fg),     C(p.Bg2)),
            Focus     = Attr(Color.Black,  C(p.Green)),
            HotNormal = Attr(C(p.Orange),  C(p.Bg2)),
            HotFocus  = Attr(Color.Black,  C(p.Green)),
            Disabled  = Attr(C(p.Comment), C(p.Bg2))
        };

        var status = new ColorScheme {
            Normal    = Attr(C(p.Fg),      C(p.Bg2)),
            Focus     = Attr(Color.Black,   C(p.Orange)),
            HotNormal = Attr(C(p.Yellow),   C(p.Bg2)),
            HotFocus  = Attr(Color.Black,   C(p.Orange)),
            Disabled  = Attr(C(p.Comment),  C(p.Bg2))
        };

        return new UserSchemes { Main = main, Menu = menu, List = list, Input = input, Dialog = dialog, Status = status };
    }

    private static Color C(string hex) => ApproxAnsi(hex);
    private static Attribute Attr(Color fg, Color bg) => new(fg, bg);

    // Terminal.Gui 1.x renders with the 16 ANSI colours. We pick the
    // nearest named colour by RGB distance rather than trying to emit
    // true-colour escapes that most terminal apps downgrade anyway.
    private static Color ApproxAnsi(string hex)
    {
        hex = (hex ?? "#000000").Trim().TrimStart('#');
        if (hex.Length < 6) hex = hex.PadRight(6, '0');
        int r = Convert.ToInt32(hex.Substring(0, 2), 16);
        int g = Convert.ToInt32(hex.Substring(2, 2), 16);
        int b = Convert.ToInt32(hex.Substring(4, 2), 16);

        var candidates = new (Color color, int r, int g, int b)[]
        {
            (Color.Black,     0,   0,   0),
            (Color.DarkGray,  85,  85,  85),
            (Color.Gray,      136, 136, 136),
            (Color.White,     255, 255, 255),
            (Color.Red,       205, 0,   0),
            (Color.Green,     0,   205, 0),
            (Color.Blue,      0,   0,   205),
            (Color.Cyan,      0,   205, 205),
            (Color.Magenta,   205, 0,   205),
            (Color.Brown,     205, 205, 0),
        };

        int Dist2((int r, int g, int b) a, (int r, int g, int b) bb)
        {
            var dr = a.r - bb.r; var dg = a.g - bb.g; var db = a.b - bb.b;
            return dr * dr + dg * dg + db * db;
        }

        var want = (r, g, b);
        var best = candidates[0];
        var bestD = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Dist2(want, (c.r, c.g, c.b));
            if (d < bestD) { best = c; bestD = d; }
        }
        return best.color;
    }
}
