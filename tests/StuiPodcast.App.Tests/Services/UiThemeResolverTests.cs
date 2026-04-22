using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.App.UI;
using Xunit;

namespace StuiPodcast.App.Tests.Services;

public sealed class UiThemeResolverTests
{
    [Theory]
    [InlineData("base",   "Base")]
    [InlineData("accent", "MenuAccent")]
    [InlineData("native", "Native")]
    [InlineData("user",   "User")]
    public void Cli_theme_maps_to_mode_and_persists_name(string cli, string expectedMode)
    {
        var r = UiThemeResolver.Resolve(cli, savedPref: null);
        r.Mode.ToString().Should().Be(expectedMode);
        r.ShouldPersistPref.Should().Be(expectedMode);
    }

    [Fact]
    public void Cli_auto_resolves_to_User_and_persists_auto_string()
    {
        var r = UiThemeResolver.Resolve("auto", savedPref: null);
        r.Mode.Should().Be(ThemeMode.User);
        r.ShouldPersistPref.Should().Be("auto");
    }

    [Fact]
    public void Cli_is_case_insensitive()
    {
        var r = UiThemeResolver.Resolve("BASE", savedPref: null);
        r.Mode.Should().Be(ThemeMode.Base);
    }

    [Fact]
    public void Cli_unknown_falls_back_to_User()
    {
        var r = UiThemeResolver.Resolve("banana", savedPref: null);
        r.Mode.Should().Be(ThemeMode.User);
    }

    [Fact]
    public void No_cli_and_saved_auto_resolves_to_User_without_persist()
    {
        var r = UiThemeResolver.Resolve(null, savedPref: "auto");
        r.Mode.Should().Be(ThemeMode.User);
        r.ShouldPersistPref.Should().BeNull();
    }

    [Fact]
    public void No_cli_and_saved_Base_resolves_to_Base_without_persist()
    {
        var r = UiThemeResolver.Resolve(null, savedPref: "Base");
        r.Mode.Should().Be(ThemeMode.Base);
        r.ShouldPersistPref.Should().BeNull();
    }

    [Fact]
    public void No_cli_and_saved_unknown_falls_back_to_User()
    {
        var r = UiThemeResolver.Resolve(null, savedPref: "nonsense");
        r.Mode.Should().Be(ThemeMode.User);
    }

    [Fact]
    public void No_cli_and_saved_null_defaults_to_auto_User()
    {
        var r = UiThemeResolver.Resolve(null, savedPref: null);
        r.Mode.Should().Be(ThemeMode.User);
    }

    [Fact]
    public void Whitespace_cli_falls_through_to_saved_pref()
    {
        var r = UiThemeResolver.Resolve("   ", savedPref: "Native");
        r.Mode.Should().Be(ThemeMode.Native);
    }

    [Fact]
    public void Cli_takes_precedence_over_saved_pref()
    {
        var r = UiThemeResolver.Resolve("base", savedPref: "Native");
        r.Mode.Should().Be(ThemeMode.Base);
    }
}
