using FluentAssertions;
using StuiPodcast.App.Command;
using StuiPodcast.App.UI;
using Terminal.Gui;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

// The menu bar is the mouse-and-discovery path into the same commands the
// keyboard reaches. A menu entry wired to a typo'd command string is
// invisible until somebody clicks it and nothing happens, so the useful test
// is to click every single one and check what comes out.
[Collection(TuiCollection.Name)]
public sealed class UiMenuBarFactoryTests
{
    private sealed class Recorder
    {
        public readonly List<string> Commands = new();
        public readonly List<string> Seeds = new();
        public int AddFeed, Quit, FocusFeeds, FocusEpisodes, OpenDetails, BackFromDetails;
        public int JumpNext, JumpPrev, ShowCommand, ToggleTheme, Refreshes;

        public UiMenuBarFactory.Callbacks Build() => new(
            Command: c => Commands.Add(c),
            RefreshRequested: () => { Refreshes++; return Task.CompletedTask; },
            AddFeed: () => AddFeed++,
            Quit: () => Quit++,
            FocusFeeds: () => FocusFeeds++,
            FocusEpisodes: () => FocusEpisodes++,
            OpenDetails: () => OpenDetails++,
            BackFromDetails: () => BackFromDetails++,
            JumpNextUnplayed: () => JumpNext++,
            JumpPrevUnplayed: () => JumpPrev++,
            ShowCommand: () => ShowCommand++,
            ShowCommandSeeded: s => Seeds.Add(s),
            ToggleTheme: () => ToggleTheme++);
    }

    // Every leaf item in the whole menu tree, separators excluded.
    private static IEnumerable<MenuItem> Leaves(MenuBar bar)
    {
        foreach (var top in bar.Menus)
            foreach (var item in Walk(top.Children))
                yield return item;

        static IEnumerable<MenuItem> Walk(MenuItem[]? children)
        {
            foreach (var c in children ?? Array.Empty<MenuItem>())
            {
                if (c is null) continue;
                if (c is MenuBarItem sub) { foreach (var d in Walk(sub.Children)) yield return d; }
                else if (c.Title?.ToString() != "-") yield return c;
            }
        }
    }

    // Fires every leaf action. A few entries open a modal dialog, and a modal
    // never returns under FakeMainLoop because nothing can close it, so each
    // action runs on its own thread and is abandoned if it blocks. The
    // entries we care about here return immediately.
    private static void ClickEverything(MenuBar bar)
    {
        foreach (var item in Leaves(bar))
        {
            var action = item.Action;
            if (action == null) continue;

            var t = new Thread(() => { try { action(); } catch { } }) { IsBackground = true };
            t.Start();
            t.Join(TimeSpan.FromMilliseconds(200));
        }
    }

    // Mirrors CmdRouter: queue and download verbs never reach the parser.
    private static bool AppAccepts(string raw)
        => CmdParser.Parse(raw).Kind != TopCommand.Unknown
           || raw.StartsWith(":dl", StringComparison.OrdinalIgnoreCase)
           || raw.StartsWith(":download", StringComparison.OrdinalIgnoreCase)
           || raw.StartsWith(":queue", StringComparison.OrdinalIgnoreCase);

    // ── structure ───────────────────────────────────────────────────────────

    [Fact]
    public void The_menu_bar_builds()
    {
        using var tui = new TuiHarness();
        var bar = UiMenuBarFactory.Build(new Recorder().Build());

        bar.Menus.Should().NotBeEmpty();
    }

    [Fact]
    public void Every_top_level_menu_has_a_title()
    {
        using var tui = new TuiHarness();
        var bar = UiMenuBarFactory.Build(new Recorder().Build());

        bar.Menus.Select(m => m.Title.ToString())
            .Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t));
    }

    [Fact]
    public void Every_entry_has_a_label()
    {
        using var tui = new TuiHarness();
        var bar = UiMenuBarFactory.Build(new Recorder().Build());

        Leaves(bar).Select(i => i.Title.ToString())
            .Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t));
    }

    [Fact]
    public void No_label_carries_a_hotkey_underscore()
    {
        // Issue #5: Terminal.Gui turns "_X" into an Alt+X hotkey and paints
        // the letter in the accent colour. People read the highlight as
        // "press this key" and it does nothing outside an open menu, so the
        // markers are gone. A stray underscore would also render literally.
        using var tui = new TuiHarness();
        var bar = UiMenuBarFactory.Build(new Recorder().Build());

        var labels = bar.Menus.Select(m => m.Title.ToString())
            .Concat(Leaves(bar).Select(i => i.Title.ToString()))
            .ToList();

        labels.Should().OnlyContain(l => !l!.Contains('_'));
    }

    [Fact]
    public void The_labels_still_name_their_commands()
    {
        // Stripping the markers must not have eaten anything else.
        using var tui = new TuiHarness();
        var bar = UiMenuBarFactory.Build(new Recorder().Build());

        var labels = Leaves(bar).Select(i => i.Title.ToString()!).ToList();

        labels.Should().Contain(l => l.Contains("All Episodes"));
        labels.Should().Contain(l => l.Contains("Quit"));
        labels.Should().Contain(l => l.Contains("Play/Pause"));
    }

    [Fact]
    public void Separators_are_real_separators()
    {
        // Terminal.Gui draws a null child as a rule. A MenuItem whose text is
        // "-" is just an entry called "-", which is what the menu used to
        // show.
        using var tui = new TuiHarness();
        var bar = UiMenuBarFactory.Build(new Recorder().Build());

        var all = bar.Menus.SelectMany(m => m.Children ?? Array.Empty<MenuItem>()).ToList();

        all.Count(i => i == null).Should().BeGreaterThan(0, "the menus do use separators");
        all.Where(i => i is not null)
           .Select(i => i!.Title.ToString())
           .Should().NotContain("-");
    }

    [Fact]
    public void The_menu_renders()
    {
        using var tui = new TuiHarness();
        var bar = UiMenuBarFactory.Build(new Recorder().Build());
        Application.Top.Add(bar);

        tui.Render();

        tui.Line(0).Should().NotBeNullOrWhiteSpace();
    }

    // ── the payload of every entry ──────────────────────────────────────────

    [Fact]
    public void No_entry_fires_a_command_the_app_does_not_know()
    {
        using var tui = new TuiHarness();
        var rec = new Recorder();
        var bar = UiMenuBarFactory.Build(rec.Build());

        ClickEverything(bar);

        rec.Commands.Where(c => !AppAccepts(c))
            .Should().BeEmpty("every menu entry must dispatch something the router accepts");
    }

    [Fact]
    public void No_entry_seeds_a_command_the_app_does_not_know()
    {
        using var tui = new TuiHarness();
        var rec = new Recorder();
        var bar = UiMenuBarFactory.Build(rec.Build());

        ClickEverything(bar);

        // Seeds are prefilled command lines like ":seek "; the verb in front
        // of the space has to be real.
        rec.Seeds.Select(s => s.Trim())
            .Where(s => !AppAccepts(s))
            .Should().BeEmpty();
    }

    [Fact]
    public void Clicking_through_the_whole_menu_does_something()
    {
        using var tui = new TuiHarness();
        var rec = new Recorder();
        var bar = UiMenuBarFactory.Build(rec.Build());

        ClickEverything(bar);

        (rec.Commands.Count + rec.Seeds.Count).Should().BeGreaterThan(10);
    }

    // ── Seed ────────────────────────────────────────────────────────────────

    [Fact]
    public void Seed_adds_the_colon_and_a_trailing_space()
        => UiMenuBarFactory.Seed("seek").Should().Be(":seek ");

    [Fact]
    public void Seed_keeps_an_existing_colon()
        => UiMenuBarFactory.Seed(":seek").Should().Be(":seek ");

    [Fact]
    public void Seed_trims_around_the_command()
        => UiMenuBarFactory.Seed("  :seek  ").Should().Be(":seek ");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Seed_of_nothing_is_a_bare_prompt(string? cmd)
        => UiMenuBarFactory.Seed(cmd!).Should().Be(":");

    [Fact]
    public void A_seeded_command_is_ready_for_arguments()
    {
        // The point of the trailing space: the user types the argument
        // straight away without having to add one.
        var seed = UiMenuBarFactory.Seed(":vol");

        seed.Should().EndWith(" ");
        CmdParser.Parse(seed + "50").Kind.Should().Be(TopCommand.Volume);
    }
}
