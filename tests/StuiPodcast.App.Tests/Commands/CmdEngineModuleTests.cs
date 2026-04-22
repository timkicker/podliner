using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdEngineModuleTests
{
    private readonly FakeAudioPlayer _player = new();
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private bool _saved;
    private Task Save() { _saved = true; return Task.CompletedTask; }

    // ── :engine show / empty ─────────────────────────────────────────────────

    [Fact]
    public void Empty_arg_shows_engine_info()
    {
        CmdEngineModule.ExecEngine(Array.Empty<string>(), _player, _ui, _data, Save);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine active"));
    }

    [Fact]
    public void Show_arg_shows_engine_info()
    {
        CmdEngineModule.ExecEngine(new[] { "show" }, _player, _ui, _data, Save);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine active") || m.Text.Contains(_player.Name));
    }

    [Fact]
    public void Show_includes_capabilities_summary()
    {
        _player.Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Seek | PlayerCapabilities.Speed;
        CmdEngineModule.ExecEngine(new[] { "show" }, _player, _ui, _data, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("seek"));
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("speed"));
    }

    // ── :engine diag ─────────────────────────────────────────────────────────

    [Fact]
    public void Diag_prints_full_diagnostics()
    {
        _data.PreferredEngine = "mpv";
        _data.LastEngineUsed = "mpv";

        CmdEngineModule.ExecEngine(new[] { "diag" }, _player, _ui, _data, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine:"));
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("caps:"));
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("pref="));
    }

    // ── :engine <preference> without switcher ───────────────────────────────

    [Theory]
    [InlineData("auto")]
    [InlineData("vlc")]
    [InlineData("mpv")]
    [InlineData("ffplay")]
    [InlineData("mediafoundation")]
    public void Valid_preferences_update_data_and_persist(string pref)
    {
        CmdEngineModule.ExecEngine(new[] { pref }, _player, _ui, _data, Save);

        _data.PreferredEngine.Should().Be(pref);
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Mf_shorthand_expands_to_mediafoundation()
    {
        CmdEngineModule.ExecEngine(new[] { "mf" }, _player, _ui, _data, Save);
        _data.PreferredEngine.Should().Be("mediafoundation");
    }

    [Fact]
    public void Valid_pref_without_switcher_just_shows_pref_osd()
    {
        CmdEngineModule.ExecEngine(new[] { "vlc" }, _player, _ui, _data, Save);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine pref"));
    }

    [Fact]
    public void Valid_pref_with_switcher_calls_it_and_shows_switching_osd()
    {
        string? switchedTo = null;
        Func<string, Task> switcher = pref => { switchedTo = pref; return Task.CompletedTask; };

        CmdEngineModule.ExecEngine(new[] { "mpv" }, _player, _ui, _data, Save, switcher);

        switchedTo.Should().Be("mpv");
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("switching"));
    }

    // ── Invalid preference ───────────────────────────────────────────────────

    [Fact]
    public void Invalid_preference_shows_usage()
    {
        CmdEngineModule.ExecEngine(new[] { "banana" }, _player, _ui, _data, Save);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
        _data.PreferredEngine.Should().NotBe("banana");
    }

    [Fact]
    public void Empty_preferred_engine_shown_as_auto_in_diag()
    {
        _data.PreferredEngine = null;
        CmdEngineModule.ExecEngine(new[] { "diag" }, _player, _ui, _data, Save);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("pref=auto"));
    }
}
