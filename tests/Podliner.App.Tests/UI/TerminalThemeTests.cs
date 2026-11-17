using FluentAssertions;
using Podliner.App.Debug;
using Podliner.App.UI;
using Serilog.Events;
using Serilog.Parsing;
using Terminal.Gui;
using Xunit;
using Attribute = Terminal.Gui.Attribute;

namespace Podliner.App.Tests.UI;

// The terminal theme (#41) may only draw with ANSI 0 and 7, the one pair
// every palette keeps readable. The report came from a matugen palette in
// which bright black is pale beige, so pale text on it all but vanished.
[Collection(TuiCollection.Name)]
public sealed class TerminalThemeTests
{
    static readonly Color[] Allowed = { Color.Black, Color.Gray };

    // ANSI 0..15. "matugen" is measured from the screenshot in #41: 0, 7 and
    // 8 are known, the rest only fill the table.
    static readonly Dictionary<string, string[]> Palettes = new()
    {
        ["xterm"]    = "000000 cd0000 00cd00 cdcd00 0000ee cd00cd 00cdcd e5e5e5 7f7f7f ff0000 00ff00 ffff00 5c5cff ff00ff 00ffff ffffff".Split(' '),
        ["vga"]      = "000000 aa0000 00aa00 aa5500 0000aa aa00aa 00aaaa aaaaaa 555555 ff5555 55ff55 ffff55 5555ff ff55ff 55ffff ffffff".Split(' '),
        ["campbell"] = "0c0c0c c50f1f 13a10e c19c00 0037da 881798 3a96dd cccccc 767676 e74856 16c60c f9f1a5 3b78ff b4009e 61d6d6 f2f2f2".Split(' '),
        ["macos"]    = "000000 990000 00a600 999900 0000b2 b200b2 00a6b2 bfbfbf 666666 e50000 00d900 e5e500 0000ff e500e5 00e5e5 e5e5e5".Split(' '),
        ["tango"]    = "2e3436 cc0000 4e9a06 c4a000 3465a4 75507b 06989a d3d7cf 555753 ef2929 8ae234 fce94f 729fcf ad7fa8 34e2e2 eeeeec".Split(' '),
        ["matugen"]  = "1c1a18 000000 000000 000000 000000 000000 000000 e5d7bf c4b7a5 000000 000000 000000 000000 000000 000000 f5ebdb".Split(' '),
    };

    static double Luminance(string hex)
    {
        double Ch(int i)
        {
            var v = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(0) + 0.7152 * Ch(2) + 0.0722 * Ch(4);
    }

    static double Contrast(string[] p, Color a, Color b)
    {
        var la = Luminance(p[(int)a]);
        var lb = Luminance(p[(int)b]);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static IEnumerable<Attribute> Attrs(ColorScheme s) => new[] { s.Normal, s.Focus, s.HotNormal, s.HotFocus, s.Disabled };

    static IEnumerable<ColorScheme> AllSchemes()
    {
        var u = UiShellColorSchemes.BuildTerminalSchemes();
        return new[] { u.Main, u.Menu, u.List, u.Dialog, u.Input, u.Status,
            UiShellColorSchemes.BuildTerminalDetailsScheme(), UiShellColorSchemes.BuildTerminalProgressScheme() };
    }

    [Fact]
    public void Every_scheme_uses_only_black_and_white()
    {
        using var tui = new TuiHarness();
        foreach (var s in AllSchemes())
        foreach (var a in Attrs(s))
        {
            a.Foreground.Should().BeOneOf(Allowed);
            a.Background.Should().BeOneOf(Allowed);
            a.Foreground.Should().NotBe(a.Background);
        }
    }

    [Fact]
    public void Text_is_readable_in_every_palette_including_the_reported_one()
    {
        using var tui = new TuiHarness();
        foreach (var s in AllSchemes())
        foreach (var a in Attrs(s))
        foreach (var (name, p) in Palettes)
            Contrast(p, a.Foreground, a.Background).Should().BeGreaterThanOrEqualTo(4.5,
                $"{a.Foreground} on {a.Background} in {name}");
    }

    [Fact]
    public void The_user_theme_details_were_unreadable_in_the_reported_palette()
    {
        // The counter-check: what #41 showed, measured the same way.
        using var tui = new TuiHarness();
        var d = UiShellColorSchemes.BuildDetailsScheme().Normal;
        Contrast(Palettes["matugen"], d.Foreground, d.Background).Should().BeLessThan(1.5);
    }

    static (Color Fg, Color Bg) CellColors(int value) => ((Color)(value >> 16), (Color)(value & 0xffff));

    void ScreenUsesOnlyBlackAndWhite(TuiHarness tui, string what)
    {
        var contents = ((FakeDriver)Application.Driver).Contents;
        for (int r = 0; r < tui.Rows; r++)
        for (int c = 0; c < tui.Cols; c++)
        {
            var (fg, bg) = CellColors(contents[r, c, 1]);
            if (!Allowed.Contains(fg) || !Allowed.Contains(bg))
                throw new Xunit.Sdk.XunitException(
                    $"{what}: cell {r},{c} is {fg} on {bg}\n{tui.Screen()}");
        }
    }

    [Fact]
    public void The_whole_screen_and_the_log_window_draw_only_black_and_white()
    {
        using var tui = new TuiHarness(120, 40);
        var sink = new MemoryLogSink();
        var parser = new MessageTemplateParser();
        for (int i = 0; i < 60; i++)
            sink.Emit(new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null,
                parser.Parse($"line-{i}"), Array.Empty<LogEventProperty>()));
        var shell = new UiShell(sink);
        shell.Build();
        shell.SetTheme(ThemeMode.Terminal);
        tui.Render();
        ScreenUsesOnlyBlackAndWhite(tui, "main screen");

        var dlg = UiShellLogsDialog.Build(sink);
        UiShell.FitToTheme(dlg, ThemeMode.Terminal);
        tui.Render(dlg);
        tui.ScreenContains("line-59").Should().BeTrue(tui.Screen());
        ScreenUsesOnlyBlackAndWhite(tui, "log window");
    }

    [Fact]
    public void Dialogs_follow_the_terminal_theme_and_go_back_after()
    {
        using var tui = new TuiHarness();
        var shell = new UiShell(new MemoryLogSink());
        shell.Build();
        shell.SetTheme(ThemeMode.User);
        tui.Render();
        var dialogBefore = Colors.Dialog.Normal;
        var menuBefore = Colors.Menu.Focus;

        shell.SetTheme(ThemeMode.Terminal);
        tui.Render();
        foreach (var s in new[] { Colors.Dialog, Colors.Menu, Colors.Base, Colors.TopLevel, Colors.Error })
        foreach (var a in Attrs(s))
        {
            a.Foreground.Should().BeOneOf(Allowed);
            a.Background.Should().BeOneOf(Allowed);
        }

        shell.SetTheme(ThemeMode.Light);
        shell.SetTheme(ThemeMode.User);
        tui.Render();
        Colors.Dialog.Normal.Should().Be(dialogBefore, "leaving the terminal theme has to put the dialog colours back");
        Colors.Menu.Focus.Should().Be(menuBefore);
    }
}
