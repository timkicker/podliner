using FluentAssertions;
using Podliner.Infra.Player;
using Xunit;

namespace Podliner.Infra.Tests.Player;

// VlcPathResolver reads and writes process environment variables, so these
// tests must not run in parallel with anything else that touches them.
[CollectionDefinition(EnvCollection.Name, DisableParallelization = true)]
public sealed class EnvCollection
{
    public const string Name = "env";
}

// The resolver branches on the running OS. The OS-specific tests below only
// assert on the platform they belong to; the CI matrix runs ubuntu, windows
// and macos, so each branch is covered somewhere.
//
// Engine detection is this project's most-reported problem area (issues #21,
// #24 and #26 were all "VLC isn't found"), which is why the resolver is worth
// pinning down even though most of it is filesystem probing.
[Collection(EnvCollection.Name)]
public sealed class VlcPathResolverTests : IDisposable
{
    private readonly string? _origPluginPath = Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH");
    private readonly string? _origLibDir = Environment.GetEnvironmentVariable("LIBVLC_LIB_DIR");
    private readonly string? _origPath = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", _origPluginPath);
        Environment.SetEnvironmentVariable("LIBVLC_LIB_DIR", _origLibDir);
        Environment.SetEnvironmentVariable("PATH", _origPath);
    }

    // ── contract that holds on every platform ───────────────────────────────

    [Fact]
    public void Apply_never_throws()
    {
        var act = () => VlcPathResolver.Apply();

        act.Should().NotThrow();
    }

    [Fact]
    public void Apply_always_returns_a_usable_result()
    {
        var r = VlcPathResolver.Apply();

        r.Should().NotBeNull();
        r.LibVlcOptions.Should().NotBeNull();
        r.Diagnose.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Apply_is_repeatable()
    {
        var first = VlcPathResolver.Apply();
        var second = VlcPathResolver.Apply();

        // Running twice must not accumulate options or change the answer;
        // EngineService calls it again on every engine hot-swap.
        second.Diagnose.Should().Be(first.Diagnose);
        second.LibVlcOptions.Should().BeEquivalentTo(first.LibVlcOptions);
    }

    [Fact]
    public void Apply_does_not_grow_PATH_on_repeated_calls()
    {
        // The resolver prepends its lib dir to PATH. Running it again must be
        // a no-op, otherwise every engine hot-swap would extend PATH forever.
        // Asserted against the state after the first call, not against the
        // ambient PATH, which may legitimately contain duplicates already.
        VlcPathResolver.Apply();
        var afterFirst = Environment.GetEnvironmentVariable("PATH") ?? "";

        VlcPathResolver.Apply();
        VlcPathResolver.Apply();
        var afterMore = Environment.GetEnvironmentVariable("PATH") ?? "";

        afterMore.Should().Be(afterFirst);
    }

    [Fact]
    public void An_option_is_emitted_only_when_a_plugin_dir_was_found()
    {
        var r = VlcPathResolver.Apply();

        if (string.IsNullOrWhiteSpace(r.PluginDir))
            r.LibVlcOptions.Should().BeEmpty();
        else
            r.LibVlcOptions.Should().ContainSingle(o => o.StartsWith("--plugin-path="));
    }

    // ── linux ───────────────────────────────────────────────────────────────

    [Fact]
    public void On_linux_an_explicit_plugin_path_is_honoured()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = Path.Combine(Path.GetTempPath(), "podliner-vlc-plugins");
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", dir);

        var r = VlcPathResolver.Apply();

        r.PluginDir.Should().Be(dir);
        r.LibVlcOptions.Should().ContainSingle().Which.Should().Be($"--plugin-path={dir}");
        r.Diagnose.Should().StartWith("linux ");
    }

    [Fact]
    public void On_linux_without_an_env_hint_nothing_is_forced()
    {
        if (!OperatingSystem.IsLinux()) return;

        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", null);

        var r = VlcPathResolver.Apply();

        // Distro packages install plugins where libvlc already looks, so the
        // resolver deliberately stays out of the way.
        r.PluginDir.Should().BeNull();
        r.LibDir.Should().BeNull();
        r.LibVlcOptions.Should().BeEmpty();
    }

    [Fact]
    public void On_linux_a_blank_env_hint_is_ignored()
    {
        if (!OperatingSystem.IsLinux()) return;

        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", "   ");

        var r = VlcPathResolver.Apply();

        r.LibVlcOptions.Should().BeEmpty();
    }

    // ── windows ─────────────────────────────────────────────────────────────

    [Fact]
    public void On_windows_an_explicit_lib_dir_wins_over_probing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), "podliner-vlc-lib");
        Environment.SetEnvironmentVariable("LIBVLC_LIB_DIR", dir);

        var r = VlcPathResolver.Apply();

        r.LibDir.Should().Be(dir);
        r.Diagnose.Should().StartWith("win ");
    }

    [Fact]
    public void On_windows_an_explicit_plugin_path_wins_over_probing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), "podliner-vlc-plugins");
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", dir);

        var r = VlcPathResolver.Apply();

        r.PluginDir.Should().Be(dir);
        r.LibVlcOptions.Should().ContainSingle(o => o.EndsWith(dir));
    }

    // ── macos ───────────────────────────────────────────────────────────────

    [Fact]
    public void On_macos_an_explicit_plugin_path_wins_over_probing()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var dir = Path.Combine(Path.GetTempPath(), "podliner-vlc-plugins");
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", dir);

        var r = VlcPathResolver.Apply();

        r.PluginDir.Should().Be(dir);
        r.Diagnose.Should().StartWith("mac ");
    }

    [Fact]
    public void On_macos_an_explicit_lib_dir_wins_over_probing()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var dir = Path.Combine(Path.GetTempPath(), "podliner-vlc-lib");
        Environment.SetEnvironmentVariable("LIBVLC_LIB_DIR", dir);

        var r = VlcPathResolver.Apply();

        r.LibDir.Should().Be(dir);
    }
}
