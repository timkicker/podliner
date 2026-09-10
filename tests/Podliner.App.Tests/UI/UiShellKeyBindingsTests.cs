using FluentAssertions;
using Podliner.App.UI;
using Terminal.Gui;
using Xunit;

namespace Podliner.App.Tests.UI;

// The keyboard map. Every shortcut in COMMANDS.md runs through here, and it
// had no coverage at all — a key silently falling through to the ListView is
// invisible until someone reports it.
//
// Bindings is a record of delegates, so the map can be driven directly with
// recorders and no mounted views.
public sealed class UiShellKeyBindingsTests
{
    private sealed class Recorder
    {
        public readonly List<string> Commands = new();
        public readonly List<int> Moves = new();
        public readonly List<int> Jumps = new();
        public int FocusFeeds, FocusEpisodes, TogglePlayed, Quit, ToggleTheme, PlaySelected, SearchNotified;
        public string? CommandBoxSeed, SearchBoxSeed, AppliedSearch;
        public string? LastSearch;
        public bool FeedsPaneActive;
        public Guid? SelectedFeedId;

        public UiShellKeyBindings.Bindings Build() => new(
            EpisodesPane: null,
            Player: null,
            GetSelectedFeedId: () => SelectedFeedId,
            IsFeedsPaneActive: () => FeedsPaneActive,
            MoveList: d => Moves.Add(d),
            FocusFeeds: () => FocusFeeds++,
            FocusEpisodes: () => FocusEpisodes++,
            JumpToUnplayed: d => Jumps.Add(d),
            InvokeCommand: c => Commands.Add(c),
            TogglePlayed: () => TogglePlayed++,
            ShowLogs: _ => { },
            Quit: () => Quit++,
            ToggleTheme: () => ToggleTheme++,
            ShowCommandBox: s => CommandBoxSeed = s,
            ShowSearchBox: s => SearchBoxSeed = s,
            PlaySelected: () => PlaySelected++,
            GetLastSearch: () => LastSearch,
            ApplySearch: s => AppliedSearch = s,
            NotifySelectedFeedChanged: () => SearchNotified++);
    }

    private static bool Press(Recorder r, Key key)
        => UiShellKeyBindings.Handle(
            new View.KeyEventEventArgs(new KeyEvent(key, new KeyModifiers())), r.Build());

    private static bool Press(Recorder r, char c) => Press(r, (Key)c);

    // ── transport ───────────────────────────────────────────────────────────

    [Fact]
    public void Space_toggles_playback()
    {
        var r = new Recorder();

        Press(r, Key.Space).Should().BeTrue();

        r.Commands.Should().Contain(":toggle");
    }

    [Theory]
    [InlineData('[', ":speed -0.1")]
    [InlineData(']', ":speed +0.1")]
    [InlineData('=', ":speed 1.0")]
    [InlineData('1', ":speed 1.0")]
    [InlineData('2', ":speed 1.25")]
    [InlineData('3', ":speed 1.5")]
    public void Speed_keys_map_to_speed_commands(char key, string expected)
    {
        var r = new Recorder();

        Press(r, key).Should().BeTrue();

        r.Commands.Should().Contain(expected);
    }

    [Theory]
    [InlineData('-', ":vol -5")]
    [InlineData('+', ":vol +5")]
    public void Volume_keys_map_to_volume_commands(char key, string expected)
    {
        var r = new Recorder();

        Press(r, key);

        r.Commands.Should().Contain(expected);
    }

    [Fact]
    public void Arrow_keys_seek_ten_seconds()
    {
        var r = new Recorder();

        Press(r, Key.CursorLeft);
        Press(r, Key.CursorRight);

        r.Commands.Should().Equal(":seek -10", ":seek +10");
    }

    [Theory]
    [InlineData('g', ":seek 0:00")]
    [InlineData('G', ":seek 100%")]
    public void G_seeks_to_the_ends(char key, string expected)
    {
        var r = new Recorder();

        Press(r, key);

        r.Commands.Should().Contain(expected);
    }

    // ── chapters ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(',', ":chapter prev")]
    [InlineData('.', ":chapter next")]
    public void Comma_and_period_move_between_chapters(char key, string expected)
    {
        var r = new Recorder();

        Press(r, key).Should().BeTrue();

        r.Commands.Should().Contain(expected);
    }

    // ── list movement ───────────────────────────────────────────────────────

    [Fact]
    public void J_and_K_move_the_selection()
    {
        var r = new Recorder();

        Press(r, 'j');
        Press(r, 'k');

        r.Moves.Should().Equal(+1, -1);
    }

    [Fact]
    public void Cursor_keys_move_the_selection_too()
    {
        var r = new Recorder();

        Press(r, Key.CursorDown);
        Press(r, Key.CursorUp);

        r.Moves.Should().Equal(+1, -1);
    }

    [Fact]
    public void H_focuses_the_feeds_pane()
    {
        var r = new Recorder();

        Press(r, 'h').Should().BeTrue();

        r.FocusFeeds.Should().Be(1);
    }

    [Fact]
    public void L_moves_from_the_feeds_pane_to_the_episodes_pane()
    {
        var r = new Recorder { FeedsPaneActive = true };

        Press(r, 'l').Should().BeTrue();

        r.FocusEpisodes.Should().Be(1);
    }

    // ── shift J and K are context sensitive ─────────────────────────────────

    [Fact]
    public void Shift_J_jumps_to_the_next_unplayed_outside_the_queue()
    {
        var r = new Recorder { SelectedFeedId = Guid.NewGuid() };

        Press(r, 'J');

        r.Jumps.Should().Equal(+1);
        r.Commands.Should().BeEmpty();
    }

    [Fact]
    public void Shift_J_reorders_inside_the_queue_view()
    {
        var r = new Recorder { SelectedFeedId = Podliner.App.Services.VirtualFeedsCatalog.Queue };

        Press(r, 'J');

        r.Commands.Should().Contain(":queue move down");
        r.Jumps.Should().BeEmpty();
    }

    [Fact]
    public void Shift_K_reorders_inside_the_queue_view()
    {
        var r = new Recorder { SelectedFeedId = Podliner.App.Services.VirtualFeedsCatalog.Queue };

        Press(r, 'K');

        r.Commands.Should().Contain(":queue move up");
    }

    // ── prompts ─────────────────────────────────────────────────────────────

    [Fact]
    public void Colon_opens_the_command_box()
    {
        var r = new Recorder();

        Press(r, ':').Should().BeTrue();

        r.CommandBoxSeed.Should().Be(":");
    }

    [Fact]
    public void Slash_opens_the_search_box()
    {
        var r = new Recorder();

        Press(r, '/').Should().BeTrue();

        r.SearchBoxSeed.Should().Be("/");
    }

    // ── repeat search ───────────────────────────────────────────────────────

    [Fact]
    public void N_repeats_the_last_search()
    {
        var r = new Recorder { LastSearch = "rust" };

        Press(r, 'n').Should().BeTrue();

        r.AppliedSearch.Should().Be("rust");
        r.SearchNotified.Should().Be(1);
    }

    [Fact]
    public void N_does_nothing_when_there_is_no_previous_search()
    {
        var r = new Recorder { LastSearch = null };

        Press(r, 'n').Should().BeFalse();

        r.AppliedSearch.Should().BeNull();
    }

    // ── misc shell keys ─────────────────────────────────────────────────────

    [Theory]
    [InlineData('q')]
    [InlineData('Q')]
    public void Q_quits(char key)
    {
        var r = new Recorder();

        Press(r, key);

        r.Quit.Should().Be(1);
    }

    [Theory]
    [InlineData('t')]
    [InlineData('T')]
    public void T_toggles_the_theme(char key)
    {
        var r = new Recorder();

        Press(r, key);

        r.ToggleTheme.Should().Be(1);
    }

    [Theory]
    [InlineData('m')]
    [InlineData('M')]
    public void M_toggles_the_played_marker(char key)
    {
        var r = new Recorder();

        Press(r, key);

        r.TogglePlayed.Should().Be(1);
    }

    [Theory]
    [InlineData('u')]
    [InlineData('U')]
    public void U_toggles_the_unplayed_filter(char key)
    {
        var r = new Recorder();

        Press(r, key);

        r.Commands.Should().Contain(":filter toggle");
    }

    [Theory]
    [InlineData('d')]
    [InlineData('D')]
    public void D_toggles_the_download(char key)
    {
        var r = new Recorder();

        Press(r, key);

        r.Commands.Should().Contain(":dl toggle");
    }

    [Fact]
    public void Ctrl_L_redraws()
    {
        var r = new Recorder();

        Press(r, Key.L | Key.CtrlMask).Should().BeTrue();

        r.Commands.Should().Contain(":redraw");
    }

    // ── enter ───────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_plays_the_selected_episode()
    {
        var r = new Recorder();

        Press(r, Key.Enter).Should().BeTrue();

        r.PlaySelected.Should().Be(1);
    }

    [Fact]
    public void Enter_in_the_feeds_pane_moves_to_the_episodes_instead()
    {
        var r = new Recorder { FeedsPaneActive = true };

        Press(r, Key.Enter).Should().BeTrue();

        r.FocusEpisodes.Should().Be(1);
        r.PlaySelected.Should().Be(0);
    }

    // ── keys the shell must not swallow ─────────────────────────────────────

    [Theory]
    [InlineData(Key.C)]
    [InlineData(Key.V)]
    [InlineData(Key.X)]
    public void Raw_ctrl_clipboard_keys_are_swallowed_on_read_only_lists(Key baseKey)
    {
        // Otherwise Terminal.Gui treats them as text-input actions on a list
        // that has no text to cut or paste.
        var r = new Recorder();

        Press(r, baseKey | Key.CtrlMask).Should().BeTrue();

        r.Commands.Should().BeEmpty();
        r.Moves.Should().BeEmpty();
    }

    [Fact]
    public void An_unbound_key_is_left_alone()
    {
        var r = new Recorder();

        Press(r, 'z').Should().BeFalse();

        r.Commands.Should().BeEmpty();
    }

    // ── leaving a search ────────────────────────────────────────────────────

    [Fact]
    public void Esc_clears_an_active_search()
    {
        var r = new Recorder { LastSearch = "letters" };

        Press(r, Key.Esc).Should().BeTrue();

        r.Commands.Should().Contain(":search clear");
    }

    [Fact]
    public void Esc_does_nothing_when_no_search_is_active()
    {
        var r = new Recorder { LastSearch = null };

        Press(r, Key.Esc);

        r.Commands.Should().NotContain(":search clear");
    }
}
