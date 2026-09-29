using FluentAssertions;
using Xunit;

namespace Podliner.App.Tests.UI;

// UiShell tells ncurses the terminal size itself when Terminal.Gui misses a
// resize (#4), through Unix.Terminal.Curses.resize_term found by reflection.
// If a Terminal.Gui update drops or renames it, that code quietly does
// nothing and #4 is back. resizeterm is the one it must NOT use: it pushes
// KEY_RESIZE into the input, and Terminal.Gui 1.19 then garbles the next key.
public sealed class CursesResizeBindingTests
{
    private static System.Reflection.MethodInfo? Find(string name)
        => typeof(Terminal.Gui.Application).Assembly.GetType("Unix.Terminal.Curses")
            ?.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

    [Fact]
    public void Terminal_Gui_still_exposes_resize_term()
    {
        var m = Find("resize_term");

        m.Should().NotBeNull("the #4 resize rescue reflects on it");
        m!.GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(int), typeof(int));
    }

    [Fact]
    public void The_resize_rescue_does_not_use_resizeterm()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Podliner.App", "UI", "UiShell.Navigation.cs"));

        source.Should().Contain("\"resize_term\"");
        source.Should().NotContain("GetMethod(\"resizeterm\"",
            "resizeterm pushes KEY_RESIZE and the first key after every resize was lost");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ROADMAP.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
