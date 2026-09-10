using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

// Decides local file vs feed URL per `:play-source`. The offline cases matter
// most: handing the engine a remote URL with no network is the difference
// between "not downloaded" in the OSD and a silent stall.
public sealed class PlaySourceResolverTests
{
    private const string Remote = "https://cdn.test/ep1.mp3";
    private const string Local  = "/home/tim/Podcasts/feed/ep1.mp3";

    private static Episode Ep() => new()
    {
        Id = Guid.NewGuid(),
        FeedId = Guid.NewGuid(),
        Title = "Episode",
        AudioUrl = Remote,
    };

    private static AppData Data(string mode, bool online) => new()
    {
        PlaySource = mode,
        NetworkOnline = online,
    };

    // ── auto ────────────────────────────────────────────────────────────────

    [Fact]
    public void Auto_prefers_the_local_file_when_one_exists()
        => PlaySourceResolver.Resolve(Data("auto", online: true), Local, Ep())
            .Should().Be(Local);

    [Fact]
    public void Auto_prefers_the_local_file_even_offline()
        => PlaySourceResolver.Resolve(Data("auto", online: false), Local, Ep())
            .Should().Be(Local);

    [Fact]
    public void Auto_falls_back_to_the_feed_url_when_online()
        => PlaySourceResolver.Resolve(Data("auto", online: true), null, Ep())
            .Should().Be(Remote);

    [Fact]
    public void Auto_gives_up_when_offline_with_no_download()
        => PlaySourceResolver.Resolve(Data("auto", online: false), null, Ep())
            .Should().BeNull();

    // ── local ───────────────────────────────────────────────────────────────

    [Fact]
    public void Local_uses_the_downloaded_file()
        => PlaySourceResolver.Resolve(Data("local", online: true), Local, Ep())
            .Should().Be(Local);

    [Fact]
    public void Local_never_reaches_for_the_network_even_when_online()
        => PlaySourceResolver.Resolve(Data("local", online: true), null, Ep())
            .Should().BeNull();

    // ── remote ──────────────────────────────────────────────────────────────

    [Fact]
    public void Remote_uses_the_feed_url_even_when_a_download_exists()
        => PlaySourceResolver.Resolve(Data("remote", online: true), Local, Ep())
            .Should().Be(Remote);

    [Fact]
    public void Remote_still_returns_the_url_when_offline()
    {
        // Deliberate: the user asked for remote, so the engine gets the URL
        // and reports the failure itself rather than the resolver second
        // guessing a possibly stale offline flag.
        PlaySourceResolver.Resolve(Data("remote", online: false), Local, Ep())
            .Should().Be(Remote);
    }

    // ── mode parsing ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LOCAL")]
    [InlineData("  local  ")]
    [InlineData("Local")]
    public void The_mode_is_case_and_whitespace_insensitive(string mode)
        => PlaySourceResolver.Resolve(Data(mode, online: true), Local, Ep())
            .Should().Be(Local);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    public void An_unknown_or_missing_mode_behaves_like_auto(string? mode)
    {
        var ep = Ep();

        PlaySourceResolver.Resolve(Data(mode!, online: true), Local, ep).Should().Be(Local);
        PlaySourceResolver.Resolve(Data(mode!, online: true), null, ep).Should().Be(Remote);
        PlaySourceResolver.Resolve(Data(mode!, online: false), null, ep).Should().BeNull();
    }

    // ── episode without audio ───────────────────────────────────────────────

    [Fact]
    public void An_episode_with_no_audio_url_yields_nothing_in_auto()
    {
        var ep = Ep();
        ep.AudioUrl = "";

        PlaySourceResolver.Resolve(Data("auto", online: true), null, ep).Should().BeEmpty();
    }
}
