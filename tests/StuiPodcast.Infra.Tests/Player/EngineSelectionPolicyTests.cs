using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Player;
using Xunit;

namespace StuiPodcast.Infra.Tests.Player;

// Engine detection is podliner's most-reported problem area: issues #11, #18,
// #21, #24 and #26 were all "the wrong engine was picked" or "no engine was
// found". The ordering now lives apart from the probing so it can be checked
// without libVLC, mpv or ffplay being installed.
public sealed class EngineSelectionPolicyTests
{
    private static IReadOnlyList<AudioEngine> Linux(AudioEngine pref = AudioEngine.Auto)
        => EngineSelectionPolicy.CandidateOrder(pref, isWindows: false);

    private static IReadOnlyList<AudioEngine> Windows(AudioEngine pref = AudioEngine.Auto)
        => EngineSelectionPolicy.CandidateOrder(pref, isWindows: true);

    // ── the auto chain ──────────────────────────────────────────────────────

    [Fact]
    public void Auto_on_linux_tries_vlc_then_mpv_then_ffplay()
        => Linux().Should().Equal(AudioEngine.Vlc, AudioEngine.Mpv, AudioEngine.Ffplay);

    [Fact]
    public void Auto_on_windows_slots_media_foundation_in_after_vlc()
        => Windows().Should().Equal(
            AudioEngine.Vlc, AudioEngine.MediaFoundation, AudioEngine.Mpv, AudioEngine.Ffplay);

    [Fact]
    public void Ffplay_is_always_last()
    {
        Linux().Last().Should().Be(AudioEngine.Ffplay);
        Windows().Last().Should().Be(AudioEngine.Ffplay);
    }

    // ── preference wins ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioEngine.Vlc)]
    [InlineData(AudioEngine.Mpv)]
    [InlineData(AudioEngine.Ffplay)]
    public void A_preferred_engine_is_tried_first_on_linux(AudioEngine pref)
        => Linux(pref).First().Should().Be(pref);

    [Theory]
    [InlineData(AudioEngine.Vlc)]
    [InlineData(AudioEngine.Mpv)]
    [InlineData(AudioEngine.Ffplay)]
    [InlineData(AudioEngine.MediaFoundation)]
    public void A_preferred_engine_is_tried_first_on_windows(AudioEngine pref)
        => Windows(pref).First().Should().Be(pref);

    [Fact]
    public void Preferring_ffplay_still_leaves_the_others_as_fallbacks()
    {
        var order = Linux(AudioEngine.Ffplay);

        order.First().Should().Be(AudioEngine.Ffplay);
        order.Should().Contain(AudioEngine.Vlc);
        order.Should().Contain(AudioEngine.Mpv);
    }

    [Fact]
    public void A_preferred_engine_appears_only_once()
    {
        // Preferring VLC must not queue VLC twice: a failing probe would then
        // be run again for nothing, and libVLC init is the slow one.
        var order = Linux(AudioEngine.Vlc);

        order.Count(e => e == AudioEngine.Vlc).Should().Be(1);
    }

    [Fact]
    public void Every_engine_appears_at_most_once()
    {
        foreach (var pref in Enum.GetValues<AudioEngine>())
        {
            Linux(pref).Should().OnlyHaveUniqueItems();
            Windows(pref).Should().OnlyHaveUniqueItems();
        }
    }

    // ── platform gating ─────────────────────────────────────────────────────

    [Fact]
    public void Media_foundation_is_never_offered_off_windows()
        => Linux().Should().NotContain(AudioEngine.MediaFoundation);

    [Fact]
    public void Preferring_media_foundation_off_windows_falls_back_to_the_normal_chain()
    {
        // A config carried over from a Windows machine must not leave the
        // user with nothing to play through.
        var order = Linux(AudioEngine.MediaFoundation);

        order.Should().NotContain(AudioEngine.MediaFoundation);
        order.Should().Equal(AudioEngine.Vlc, AudioEngine.Mpv, AudioEngine.Ffplay);
    }

    // ── invariants ──────────────────────────────────────────────────────────

    [Fact]
    public void Auto_is_never_a_candidate()
    {
        foreach (var pref in Enum.GetValues<AudioEngine>())
        {
            Linux(pref).Should().NotContain(AudioEngine.Auto);
            Windows(pref).Should().NotContain(AudioEngine.Auto);
        }
    }

    [Fact]
    public void There_is_always_something_to_try()
    {
        foreach (var pref in Enum.GetValues<AudioEngine>())
        {
            Linux(pref).Should().NotBeEmpty();
            Windows(pref).Should().NotBeEmpty();
        }
    }

    [Fact]
    public void Every_real_engine_is_reachable_on_its_platform()
    {
        Linux().Should().Contain(new[] { AudioEngine.Vlc, AudioEngine.Mpv, AudioEngine.Ffplay });
        Windows().Should().Contain(new[]
        {
            AudioEngine.Vlc, AudioEngine.MediaFoundation, AudioEngine.Mpv, AudioEngine.Ffplay
        });
    }

    // ── degraded flag ───────────────────────────────────────────────────────

    [Fact]
    public void Only_ffplay_counts_as_degraded()
    {
        EngineSelectionPolicy.IsDegraded(AudioEngine.Ffplay).Should().BeTrue();

        foreach (var e in new[]
                 {
                     AudioEngine.Auto, AudioEngine.Vlc, AudioEngine.Mpv, AudioEngine.MediaFoundation
                 })
            EngineSelectionPolicy.IsDegraded(e).Should().BeFalse();
    }
}
