using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Podliner.App.UI;

// Pure colour-scheme factory for the "User" and "Light" themes. Kept as a static helper
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

    // Light theme. Terminal.Gui 1.x only knows the 16 named colours, so the
    // slots are picked by WCAG contrast across the xterm, VGA, Windows
    // Campbell, macOS Terminal and GNOME Tango palettes (see
    // LightThemeContrastTests): text Black on White, accent text Blue, the
    // cursor and other "you are here" marks White on that same Blue and the
    // bar fill Blue on a light grey track. No yellow or orange at all: as
    // text on white it is unreadable, as a background it glares, and the
    // plain yellow slot is too dark for black text in the VGA palette.
    internal static class Light
    {
        public const Color Bg     = Color.White;
        public const Color Fg     = Color.Black;
        public const Color Muted  = Color.DarkGray;
        public const Color Accent = Color.Blue;
        public const Color Mark   = Color.Blue;
        public const Color OnMark = Color.White;
        public const Color Field  = Color.Gray;
        public const Color Track  = Color.Gray;
        public const Color Error  = Color.Red;
    }

    private static ColorScheme LightScheme(Color hot, Color focus = Light.Mark) => new()
    {
        Normal    = Attr(Light.Fg,    Light.Bg),
        Focus     = focus == Light.Mark ? Attr(Light.OnMark, Light.Mark) : Attr(Light.Fg, focus),
        HotNormal = Attr(hot,         Light.Bg),
        HotFocus  = Attr(Light.OnMark, Light.Mark),
        Disabled  = Attr(Light.Muted, Light.Bg)
    };

    public static UserSchemes BuildLightSchemes()
    {
        var field = Attr(Light.Fg, Light.Field);
        return new UserSchemes
        {
            Main   = LightScheme(Light.Accent),
            Menu   = LightScheme(Light.Accent),
            List   = LightScheme(Light.Accent),
            Dialog = LightScheme(Light.Accent),
            Input  = new ColorScheme { Normal = field, Focus = field, HotNormal = field, HotFocus = field, Disabled = field },
            Status = BuildLightProgressScheme()
        };
    }

    public static ColorScheme BuildLightDetailsScheme()
    {
        var normal = Attr(Light.Fg, Light.Bg);
        var hot    = Attr(Light.Accent, Light.Bg);
        return new ColorScheme { Normal = normal, Focus = normal, HotNormal = hot, HotFocus = hot, Disabled = Attr(Light.Muted, Light.Bg) };
    }

    // The bar draws spaces in Normal and the filled part as █ in HotNormal's
    // foreground: a blue fill on a light grey track.
    public static ColorScheme BuildLightProgressScheme()
    {
        var track = Attr(Light.Fg, Light.Track);
        var fill  = Attr(Light.Mark, Light.Track);
        return new ColorScheme { Normal = track, Focus = track, HotNormal = fill, HotFocus = fill, Disabled = track };
    }

    // Dialogs, message boxes, the help browser and a few labels use the global
    // Terminal.Gui schemes directly, and views keep a reference to those
    // objects. So the light theme writes its colours into them in place and
    // puts the originals back when another theme is picked. Terminal.Gui fills
    // a focused text field or text view with Focus, so outside the menus Focus
    // is the grey field colour; a yellow one would flood the help search and
    // the log view.
    static readonly string[] GlobalSchemes = { "TopLevel", "Base", "Dialog", "Menu", "Error" };
    static readonly Dictionary<string, (ColorScheme Scheme, Attribute[] Attrs)> _globalDefaults = new();

    public static void ApplyLightGlobals(bool on)
    {
        foreach (var name in GlobalSchemes)
        {
            if (!Colors.ColorSchemes.TryGetValue(name, out var scheme) || scheme == null) continue;

            // Application.Init hands out fresh scheme objects, so a snapshot
            // only counts for the object it was taken from.
            var known = _globalDefaults.TryGetValue(name, out var d) && ReferenceEquals(d.Scheme, scheme);
            if (on)
            {
                if (!known) _globalDefaults[name] = (scheme, Snapshot(scheme));
                Write(scheme, Snapshot(name switch
                {
                    "Menu"  => LightScheme(Light.Accent),
                    "Error" => LightScheme(Light.Error, Light.Field),
                    _       => LightScheme(Light.Accent, Light.Field)
                }));
            }
            else if (known)
            {
                Write(scheme, d.Attrs);
            }
        }
    }

    static Attribute[] Snapshot(ColorScheme s) => new[] { s.Normal, s.Focus, s.HotNormal, s.HotFocus, s.Disabled };

    static void Write(ColorScheme s, Attribute[] a)
    {
        s.Normal = a[0]; s.Focus = a[1]; s.HotNormal = a[2]; s.HotFocus = a[3]; s.Disabled = a[4];
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
