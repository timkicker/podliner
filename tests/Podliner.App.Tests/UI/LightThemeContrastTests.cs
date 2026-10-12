using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.UI;
using Terminal.Gui;
using Xunit;
using Attribute = Terminal.Gui.Attribute;

namespace Podliner.App.Tests.UI;

// The light theme can only use the 16 named colours, and what they look like
// is up to the terminal. So the contrast is measured in the palettes most
// people actually run: xterm, the VGA/Linux console, Windows Terminal's
// Campbell, macOS Terminal and GNOME's Tango. WCAG asks 4.5:1 for text and
// 3:1 for graphics such as the progress bar.
[Collection(TuiCollection.Name)]
public sealed class LightThemeContrastTests
{
    // ANSI 0..15 per palette.
    private static readonly Dictionary<string, string[]> Palettes = new()
    {
        ["xterm"]    = "000000 cd0000 00cd00 cdcd00 0000ee cd00cd 00cdcd e5e5e5 7f7f7f ff0000 00ff00 ffff00 5c5cff ff00ff 00ffff ffffff".Split(' '),
        ["vga"]      = "000000 aa0000 00aa00 aa5500 0000aa aa00aa 00aaaa aaaaaa 555555 ff5555 55ff55 ffff55 5555ff ff55ff 55ffff ffffff".Split(' '),
        ["campbell"] = "0c0c0c c50f1f 13a10e c19c00 0037da 881798 3a96dd cccccc 767676 e74856 16c60c f9f1a5 3b78ff b4009e 61d6d6 f2f2f2".Split(' '),
        ["macos"]    = "000000 990000 00a600 999900 0000b2 b200b2 00a6b2 bfbfbf 666666 e50000 00d900 e5e500 0000ff e500e5 00e5e5 e5e5e5".Split(' '),
        ["tango"]    = "2e3436 cc0000 4e9a06 c4a000 3465a4 75507b 06989a d3d7cf 555753 ef2929 8ae234 fce94f 729fcf ad7fa8 34e2e2 eeeeec".Split(' '),
    };

    private static int Ansi(Color c) => c switch
    {
        Color.Black => 0, Color.Red => 1, Color.Green => 2, Color.Brown => 3,
        Color.Blue => 4, Color.Magenta => 5, Color.Cyan => 6, Color.Gray => 7,
        Color.DarkGray => 8, Color.BrightRed => 9, Color.BrightGreen => 10, Color.BrightYellow => 11,
        Color.BrightBlue => 12, Color.BrightMagenta => 13, Color.BrightCyan => 14, Color.White => 15,
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    };

    private static double Luminance(string hex)
    {
        double Ch(int i)
        {
            var v = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(0) + 0.7152 * Ch(2) + 0.0722 * Ch(4);
    }

    private static double Contrast(string[] palette, Color a, Color b)
    {
        var la = Luminance(palette[Ansi(a)]);
        var lb = Luminance(palette[Ansi(b)]);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static void TextReadable(string what, Attribute a)
    {
        foreach (var (name, p) in Palettes)
            Contrast(p, a.Foreground, a.Background).Should().BeGreaterThanOrEqualTo(4.5,
                $"{what} ({a.Foreground} on {a.Background}) has to be readable in the {name} palette");
    }

    private static IEnumerable<(string, Attribute)> TextAttrs(string scheme, ColorScheme s) => new[]
    {
        ($"{scheme}.Normal", s.Normal), ($"{scheme}.Focus", s.Focus),
        ($"{scheme}.HotNormal", s.HotNormal), ($"{scheme}.HotFocus", s.HotFocus),
    };

    [Fact]
    public void Every_text_colour_of_the_light_theme_is_readable()
    {
        using var tui = new TuiHarness();
        var u = UiShellColorSchemes.BuildLightSchemes();

        var all = TextAttrs("Main", u.Main)
            .Concat(TextAttrs("Menu", u.Menu))
            .Concat(TextAttrs("List", u.List))
            .Concat(TextAttrs("Dialog", u.Dialog))
            .Concat(TextAttrs("Input", u.Input))
            .Concat(TextAttrs("Details", UiShellColorSchemes.BuildLightDetailsScheme()));

        foreach (var (what, attr) in all) TextReadable(what, attr);
    }

    [Fact]
    public void Disabled_text_still_clears_the_graphics_threshold()
    {
        // WCAG exempts disabled controls from 4.5:1, but greyed out should
        // still mean faint, not gone.
        using var tui = new TuiHarness();
        var d = UiShellColorSchemes.BuildLightSchemes().Main.Disabled;

        foreach (var (name, p) in Palettes)
            Contrast(p, d.Foreground, d.Background).Should().BeGreaterThanOrEqualTo(3.0, name);
    }

    [Fact]
    public void The_progress_fill_stands_out_from_its_track_and_the_page()
    {
        using var tui = new TuiHarness();
        var bar = UiShellColorSchemes.BuildLightProgressScheme();

        foreach (var (name, p) in Palettes)
        {
            Contrast(p, bar.HotNormal.Foreground, bar.Normal.Background).Should().BeGreaterThanOrEqualTo(3.0,
                $"the filled part has to be visible against the track in {name}");
            Contrast(p, bar.HotNormal.Foreground, UiShellColorSchemes.Light.Bg).Should().BeGreaterThanOrEqualTo(3.0,
                $"the filled part has to be visible against the page in {name}");
        }
        // The track is a quiet grey on purpose, so it only has to be a
        // different slot from the page.
        bar.Normal.Background.Should().NotBe(UiShellColorSchemes.Light.Bg);
    }

    [Fact]
    public void The_light_theme_uses_no_yellow()
    {
        // Bright yellow and cream areas glare on a light page.
        using var tui = new TuiHarness();
        var u = UiShellColorSchemes.BuildLightSchemes();
        var schemes = new[] { u.Main, u.Menu, u.List, u.Dialog, u.Input, u.Status,
            UiShellColorSchemes.BuildLightDetailsScheme(), UiShellColorSchemes.BuildLightProgressScheme() };

        foreach (var s in schemes)
        foreach (var a in new[] { s.Normal, s.Focus, s.HotNormal, s.HotFocus, s.Disabled })
        {
            a.Foreground.Should().NotBe(Color.BrightYellow).And.NotBe(Color.Brown);
            a.Background.Should().NotBe(Color.BrightYellow).And.NotBe(Color.Brown);
        }
    }

    [Fact]
    public void The_light_theme_has_a_light_page_and_dark_text()
    {
        // Not the dark theme turned around: the page is the lightest slot and
        // the text the darkest one in every palette.
        foreach (var (name, p) in Palettes)
            Luminance(p[Ansi(UiShellColorSchemes.Light.Bg)]).Should().BeGreaterThan(Luminance(p[Ansi(UiShellColorSchemes.Light.Fg)]) + 0.5, name);
    }

    [Fact]
    public void Dialogs_and_menus_follow_the_light_theme_and_go_back_after()
    {
        using var tui = new TuiHarness();
        var shell = new UiShell(new MemoryLogSink());
        shell.Build();
        shell.SetTheme(ThemeMode.User);
        tui.Render();
        var dialogBefore = Colors.Dialog.Normal;
        var menuBefore = Colors.Menu.Focus;

        shell.SetTheme(ThemeMode.Light);
        tui.Render();
        Colors.Dialog.Normal.Foreground.Should().Be(UiShellColorSchemes.Light.Fg);
        Colors.Dialog.Normal.Background.Should().Be(UiShellColorSchemes.Light.Bg);
        Colors.Base.Normal.Background.Should().Be(UiShellColorSchemes.Light.Bg);
        Colors.Menu.Focus.Background.Should().Be(UiShellColorSchemes.Light.Mark);
        Colors.Dialog.Focus.Background.Should().Be(UiShellColorSchemes.Light.Field,
            "a focused text field is filled with Focus, so it has to stay a quiet field colour");
        foreach (var (what, attr) in TextAttrs("Error", Colors.Error).Concat(TextAttrs("Dialog", Colors.Dialog)))
            TextReadable(what, attr);

        shell.SetTheme(ThemeMode.User);
        tui.Render();
        Colors.Dialog.Normal.Should().Be(dialogBefore, "leaving the light theme has to put the dialog colours back");
        Colors.Menu.Focus.Should().Be(menuBefore);
    }
}
