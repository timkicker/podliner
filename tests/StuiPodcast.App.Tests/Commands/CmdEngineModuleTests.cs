using FluentAssertions;
using StuiPodcast.App.Command.UseCases;
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

    private EngineUseCase Make(Func<AudioEngine, Task>? switcher = null)
    {
        Task Save() { _saved = true; return Task.CompletedTask; }
        return new EngineUseCase(_player, _ui, _data, Save, switcher);
    }

    // ── :engine show / empty ─────────────────────────────────────────────────

    [Fact]
    public void Empty_arg_shows_engine_info()
    {
        Make().Exec(Array.Empty<string>());
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine active"));
    }

    [Fact]
    public void Show_arg_shows_engine_info()
    {
        Make().Exec(new[] { "show" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine active") || m.Text.Contains(_player.Name));
    }

    [Fact]
    public void Show_includes_capabilities_summary()
    {
        _player.Capabilities = PlayerCapabilities.Play | PlayerCapabilities.Seek | PlayerCapabilities.Speed;
        Make().Exec(new[] { "show" });

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("seek"));
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("speed"));
    }

    // ── :engine diag ─────────────────────────────────────────────────────────

    [Fact]
    public void Diag_prints_full_diagnostics()
    {
        _data.PreferredEngine = AudioEngine.Mpv;
        _data.LastEngineUsed = AudioEngine.Mpv;

        Make().Exec(new[] { "diag" });

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine:"));
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("caps:"));
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("pref="));
    }

    // ── :engine <preference> without switcher ───────────────────────────────

    [Theory]
    [InlineData("auto", AudioEngine.Auto)]
    [InlineData("vlc", AudioEngine.Vlc)]
    [InlineData("mpv", AudioEngine.Mpv)]
    [InlineData("ffplay", AudioEngine.Ffplay)]
    [InlineData("mediafoundation", AudioEngine.MediaFoundation)]
    public void Valid_preferences_update_data_and_persist(string pref, AudioEngine expected)
    {
        Make().Exec(new[] { pref });

        _data.PreferredEngine.Should().Be(expected);
        _saved.Should().BeTrue();
    }

    [Fact]
    public void Mf_shorthand_expands_to_mediafoundation()
    {
        Make().Exec(new[] { "mf" });
        _data.PreferredEngine.Should().Be(AudioEngine.MediaFoundation);
    }

    [Fact]
    public void Valid_pref_without_switcher_just_shows_pref_osd()
    {
        Make().Exec(new[] { "vlc" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("engine pref"));
    }

    [Fact]
    public void Valid_pref_with_switcher_calls_it_and_shows_switching_osd()
    {
        AudioEngine? switchedTo = null;
        Func<AudioEngine, Task> switcher = pref => { switchedTo = pref; return Task.CompletedTask; };

        Make(switcher).Exec(new[] { "mpv" });

        switchedTo.Should().Be(AudioEngine.Mpv);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("switching"));
    }

    // ── Invalid preference ───────────────────────────────────────────────────

    [Fact]
    public void Invalid_preference_shows_usage()
    {
        var before = _data.PreferredEngine;
        Make().Exec(new[] { "banana" });
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
        _data.PreferredEngine.Should().Be(before);
    }

    [Fact]
    public void Default_preferred_engine_shown_as_auto_in_diag()
    {
        _data.PreferredEngine = AudioEngine.Auto;
        Make().Exec(new[] { "diag" });

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("pref=auto"));
    }
}
