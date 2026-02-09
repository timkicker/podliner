using FluentAssertions;
using StuiPodcast.Infra.Download;
using System.Text;
using Xunit;

namespace StuiPodcast.Infra.Tests.Paths;

public sealed class DownloadPathSanitizerTests
{
    static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

    [Fact]
    public void Sanitized_name_has_no_separators_or_control_chars()
    {
        var input = "A/B\\C\u0001\u0002\u0003";
        var output = DownloadPathSanitizer.SanitizeFileName(input);

        output.Should().NotContain("/");
        output.Should().NotContain("\\");
        output.Any(c => char.IsControl(c)).Should().BeFalse();
    }

    [Fact]
    public void Sanitized_name_removes_platform_invalid_chars()
    {
        var input = string.Concat(InvalidFileNameChars.Take(10)) + "hello" + string.Concat(InvalidPathChars.Take(10));
        var output = DownloadPathSanitizer.SanitizeFileName(input);

        foreach (var ch in InvalidFileNameChars.Concat(InvalidPathChars))
            output.Should().NotContain(ch.ToString());
    }

    [Fact]
    public void Enforces_utf8_byte_limit()
    {
        // create a long string with multi-byte chars
        var input = string.Concat(Enumerable.Repeat("ä", 400));
        var output = DownloadPathSanitizer.SanitizeFileName(input, maxBytesUtf8: 120);

        Encoding.UTF8.GetByteCount(output).Should().BeLessOrEqualTo(120);
        output.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Windows_reserved_names_are_not_returned_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        DownloadPathSanitizer.SanitizeFileName("CON").Should().NotBe("CON");
        DownloadPathSanitizer.SanitizeFileName("NUL.txt").Should().NotBe("NUL.txt");
        DownloadPathSanitizer.SanitizeFileName("COM1").Should().NotBe("COM1");
    }

    [Fact]
    public void Trailing_dots_and_spaces_are_stripped_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        DownloadPathSanitizer.SanitizeFileName("abc. ").Should().Be("abc");
    }
}
