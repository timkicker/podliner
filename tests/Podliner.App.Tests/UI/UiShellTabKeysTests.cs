using FluentAssertions;
using Podliner.App.UI;
using Podliner.App.UI.Controls;
using Terminal.Gui;
using Xunit;

namespace Podliner.App.Tests.UI;

// The episodes pane has three tabs: Episodes, Details, Chapters. h and l walk
// them. Chapters was only reachable with the mouse, which in a TUI means it
// was not reachable, and chapters are the headline feature of v1.2.
[Collection(TuiCollection.Name)]
public sealed class UiShellTabKeysTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TuiHarness Tui = new(100, 30);
        public readonly UiEpisodesPane Pane = new();
        public int FocusFeeds, FocusEpisodes;
        public bool FeedsPaneActive;

        public Rig()
        {
            Application.Top.Add(Pane.Tabs);
            Tui.Render();
        }

        public UiShellKeyBindings.Bindings Build() => new(
            EpisodesPane: Pane,
            Player: null,
            GetSelectedFeedId: () => null,
            IsFeedsPaneActive: () => FeedsPaneActive,
            MoveList: _ => { },
            FocusFeeds: () => FocusFeeds++,
            FocusEpisodes: () => FocusEpisodes++,
            JumpToUnplayed: _ => { },
            InvokeCommand: _ => { },
            TogglePlayed: () => { },
            ShowLogs: _ => { },
            Quit: () => { },
            ToggleTheme: () => { },
            ShowCommandBox: _ => { },
            ShowSearchBox: _ => { },
            PlaySelected: () => { },
            GetLastSearch: () => null,
            ApplySearch: _ => { },
            NotifySelectedFeedChanged: () => { });

        public void Press(char c)
            => UiShellKeyBindings.Handle(
                new View.KeyEventEventArgs(new KeyEvent((Key)c, new KeyModifiers())), Build());

        public string Tab => Pane.Tabs.SelectedTab == Pane.EpisodesTab ? "episodes"
                           : Pane.Tabs.SelectedTab == Pane.DetailsTab  ? "details"
                           : Pane.Tabs.SelectedTab == Pane.ChaptersTab ? "chapters"
                           : "?";

        public void Dispose() => Tui.Dispose();
    }

    [Fact]
    public void L_walks_forward_through_every_tab()
    {
        using var r = new Rig();
        r.Tab.Should().Be("episodes");

        r.Press('l');
        r.Tab.Should().Be("details");

        r.Press('l');
        r.Tab.Should().Be("chapters", "otherwise the chapters tab has no keyboard route at all");

        r.Press('l');
        r.Tab.Should().Be("chapters", "there is nothing past the last tab");
    }

    [Fact]
    public void H_walks_back_through_every_tab()
    {
        using var r = new Rig();
        r.Press('l');
        r.Press('l');
        r.Tab.Should().Be("chapters");

        r.Press('h');
        r.Tab.Should().Be("details");

        r.Press('h');
        r.Tab.Should().Be("episodes");
    }

    [Fact]
    public void H_on_the_episode_list_still_goes_to_the_feeds_pane()
    {
        using var r = new Rig();

        r.Press('h');

        r.FocusFeeds.Should().Be(1);
        r.Tab.Should().Be("episodes");
    }

    [Fact]
    public void L_in_the_feeds_pane_still_enters_the_episode_list()
    {
        using var r = new Rig();
        r.FeedsPaneActive = true;

        r.Press('l');

        r.FocusEpisodes.Should().Be(1);
        r.Tab.Should().Be("episodes");
    }
}
