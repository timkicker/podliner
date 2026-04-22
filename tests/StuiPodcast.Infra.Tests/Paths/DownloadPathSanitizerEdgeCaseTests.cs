using FluentAssertions;
using StuiPodcast.Infra.Download;
using Xunit;

namespace StuiPodcast.Infra.Tests.Paths;

public sealed class DownloadPathSanitizerEdgeCaseTests : IDisposable
{
    private readonly string _dir;

    public DownloadPathSanitizerEdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-pathsan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── SanitizeFileName ─────────────────────────────────────────────────────

    [Fact]
    public void Null_input_returns_safe_default()
    {
        var s = DownloadPathSanitizer.SanitizeFileName(null);
        s.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Empty_input_returns_safe_default()
    {
        var s = DownloadPathSanitizer.SanitizeFileName("");
        s.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Whitespace_only_input_returns_safe_default()
    {
        var s = DownloadPathSanitizer.SanitizeFileName("   ");
        s.Should().NotBeNullOrWhiteSpace();
    }

    // ── GetExtension ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("file.mp3", ".mp3")]
    [InlineData("file.MP3", ".mp3")]
    [InlineData("https://x.com/ep.m4a", ".m4a")]
    [InlineData("https://x.com/ep.ogg?x=y", ".ogg")]
    [InlineData("https://x.com/ep.opus?foo=bar&baz", ".opus")]
    public void GetExtension_returns_normalized_lowercase(string input, string expected)
    {
        DownloadPathSanitizer.GetExtension(input).Should().Be(expected);
    }

    [Fact]
    public void GetExtension_null_uses_fallback()
    {
        DownloadPathSanitizer.GetExtension(null, ".mp3").Should().Be(".mp3");
    }

    [Fact]
    public void GetExtension_no_extension_uses_fallback()
    {
        DownloadPathSanitizer.GetExtension("noextension", ".mp3").Should().Be(".mp3");
    }

    // ── EnsureUniquePath ─────────────────────────────────────────────────────

    [Fact]
    public void EnsureUniquePath_returns_same_when_file_does_not_exist()
    {
        var path = Path.Combine(_dir, "fresh.mp3");
        DownloadPathSanitizer.EnsureUniquePath(path).Should().Be(path);
    }

    [Fact]
    public void EnsureUniquePath_adds_suffix_when_file_exists()
    {
        var original = Path.Combine(_dir, "taken.mp3");
        File.WriteAllText(original, "x");

        var unique = DownloadPathSanitizer.EnsureUniquePath(original);

        unique.Should().NotBe(original);
        unique.Should().Contain("taken");
        unique.Should().EndWith(".mp3");
    }

    [Fact]
    public void EnsureUniquePath_handles_multiple_existing_collisions()
    {
        var original = Path.Combine(_dir, "coll.mp3");
        File.WriteAllText(original, "x");
        File.WriteAllText(Path.Combine(_dir, "coll (1).mp3"), "x");
        File.WriteAllText(Path.Combine(_dir, "coll (2).mp3"), "x");

        var unique = DownloadPathSanitizer.EnsureUniquePath(original);

        File.Exists(unique).Should().BeFalse();
    }

    // ── BuildDownloadPath ────────────────────────────────────────────────────

    [Fact]
    public void BuildDownloadPath_creates_feed_subfolder()
    {
        var path = DownloadPathSanitizer.BuildDownloadPath(
            _dir, feedTitle: "My Feed", episodeTitle: "Ep 1", urlOrExtHint: "https://x.com/ep.mp3");

        Path.GetDirectoryName(path).Should().Contain("My Feed");
        Path.GetExtension(path).Should().Be(".mp3");
    }

    [Fact]
    public void BuildDownloadPath_sanitizes_path_separators_in_titles()
    {
        // / and \ are always invalid across platforms. Other chars like |,:,?
        // are platform-specific (only invalid on Windows) so we don't assert those.
        var path = DownloadPathSanitizer.BuildDownloadPath(
            _dir, feedTitle: "Feed/With\\Slashes", episodeTitle: "Ep/Slash\\Back", urlOrExtHint: "https://x.com/ep.mp3");

        var file = Path.GetFileName(path);
        file.Should().NotContain("/");
        file.Should().NotContain("\\");
    }

    // ── SanitizeDirectoryName ────────────────────────────────────────────────

    [Fact]
    public void SanitizeDirectoryName_removes_separators()
    {
        var s = DownloadPathSanitizer.SanitizeDirectoryName("Foo/Bar\\Baz");
        s.Should().NotContain("/");
        s.Should().NotContain("\\");
    }

    [Fact]
    public void SanitizeDirectoryName_falls_back_for_empty()
    {
        DownloadPathSanitizer.SanitizeDirectoryName("").Should().NotBeNullOrWhiteSpace();
    }
}
