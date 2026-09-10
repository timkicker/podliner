using System.Text;
using FluentAssertions;
using StuiPodcast.Infra.Opml;
using Xunit;

namespace StuiPodcast.Infra.Tests.Opml;

public sealed class OpmlIoTests : IDisposable
{
    private readonly string _dir;

    public OpmlIoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-opml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    // ── ReadFile ────────────────────────────────────────────────────────────

    [Fact]
    public void ReadFile_reads_plain_utf8()
    {
        var path = P("in.opml");
        File.WriteAllText(path, "<opml>Käse</opml>", new UTF8Encoding(false));

        OpmlIo.ReadFile(path).Should().Be("<opml>Käse</opml>");
    }

    [Fact]
    public void ReadFile_strips_a_utf8_bom()
    {
        var path = P("bom.opml");
        File.WriteAllText(path, "<opml/>", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // Exporters on Windows often emit a BOM; it must not survive into
        // the string handed to the XML parser.
        OpmlIo.ReadFile(path).Should().Be("<opml/>");
        OpmlIo.ReadFile(path).Should().NotStartWith("﻿");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadFile_rejects_an_empty_path(string? path)
    {
        var act = () => OpmlIo.ReadFile(path!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReadFile_throws_when_the_file_is_missing()
    {
        var act = () => OpmlIo.ReadFile(P("nope.opml"));

        act.Should().Throw<FileNotFoundException>();
    }

    // ── WriteFile ───────────────────────────────────────────────────────────

    [Fact]
    public void WriteFile_writes_utf8_without_a_bom()
    {
        var written = OpmlIo.WriteFile(P("out.opml"), "<opml/>");

        var bytes = File.ReadAllBytes(written);
        bytes.Take(3).Should().NotBeEquivalentTo(new byte[] { 0xEF, 0xBB, 0xBF });
        File.ReadAllText(written).Should().Be("<opml/>");
    }

    [Fact]
    public void WriteFile_returns_the_path_it_actually_used()
    {
        var written = OpmlIo.WriteFile(P("result.opml"), "<opml/>");

        written.Should().EndWith("result.opml");
        File.Exists(written).Should().BeTrue();
    }

    [Fact]
    public void WriteFile_appends_the_opml_extension_when_missing()
    {
        var written = OpmlIo.WriteFile(P("noext"), "<opml/>");

        Path.GetFileName(written).Should().Be("noext.opml");
    }

    [Fact]
    public void WriteFile_forces_the_opml_extension_over_another_one()
    {
        var written = OpmlIo.WriteFile(P("feeds.txt"), "<opml/>");

        Path.GetFileName(written).Should().Be("feeds.opml");
    }

    [Fact]
    public void WriteFile_keeps_an_existing_opml_extension()
    {
        var written = OpmlIo.WriteFile(P("keep.opml"), "<opml/>");

        Path.GetFileName(written).Should().Be("keep.opml");
    }

    [Fact]
    public void WriteFile_creates_missing_directories()
    {
        var nested = Path.Combine(_dir, "a", "b", "c", "deep.opml");

        var written = OpmlIo.WriteFile(nested, "<opml/>");

        File.Exists(written).Should().BeTrue();
    }

    [Fact]
    public void WriteFile_overwrites_by_default()
    {
        var path = P("twice.opml");
        OpmlIo.WriteFile(path, "<first/>");

        var written = OpmlIo.WriteFile(path, "<second/>");

        File.ReadAllText(written).Should().Be("<second/>");
    }

    [Fact]
    public void WriteFile_refuses_to_overwrite_when_told_not_to()
    {
        var path = P("guarded.opml");
        OpmlIo.WriteFile(path, "<first/>");

        var act = () => OpmlIo.WriteFile(path, "<second/>", overwrite: false);

        act.Should().Throw<IOException>();
        File.ReadAllText(path).Should().Be("<first/>");
    }

    [Fact]
    public void WriteFile_leaves_no_temp_file_behind()
    {
        var written = OpmlIo.WriteFile(P("clean.opml"), "<opml/>");

        File.Exists(written + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void WriteFile_treats_null_content_as_empty()
    {
        var written = OpmlIo.WriteFile(P("empty.opml"), null!);

        File.ReadAllText(written).Should().BeEmpty();
    }

    [Fact]
    public void WriteFile_rejects_a_null_path()
    {
        var act = () => OpmlIo.WriteFile(null!, "<opml/>");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WriteFile_can_skip_sanitizing()
    {
        var written = OpmlIo.WriteFile(P("raw.xml"), "<opml/>", sanitizeFileNameIfNeeded: false);

        Path.GetFileName(written).Should().Be("raw.xml");
    }

    [Fact]
    public void A_written_file_reads_back_unchanged()
    {
        var content = "<opml version=\"2.0\"><body><outline text=\"Käse &amp; Brot\"/></body></opml>";

        var written = OpmlIo.WriteFile(P("roundtrip.opml"), content);

        OpmlIo.ReadFile(written).Should().Be(content);
    }

    // ── GetDefaultExportPath ────────────────────────────────────────────────

    [Fact]
    public void GetDefaultExportPath_uses_the_given_directory()
    {
        var path = OpmlIo.GetDefaultExportPath(_dir);

        Path.GetDirectoryName(path).Should().Be(_dir);
        Path.GetFileName(path).Should().Be("podliner-feeds.opml");
    }

    [Fact]
    public void GetDefaultExportPath_falls_back_to_the_default_name()
    {
        var path = OpmlIo.GetDefaultExportPath(_dir, baseName: "   ");

        Path.GetFileName(path).Should().Be("podliner-feeds.opml");
    }

    [Fact]
    public void GetDefaultExportPath_appends_opml_to_a_bare_name()
    {
        var path = OpmlIo.GetDefaultExportPath(_dir, baseName: "mysubs");

        Path.GetFileName(path).Should().Be("mysubs.opml");
    }

    [Fact]
    public void GetDefaultExportPath_creates_the_directory()
    {
        var target = Path.Combine(_dir, "fresh");

        OpmlIo.GetDefaultExportPath(target);

        Directory.Exists(target).Should().BeTrue();
    }

    [Fact]
    public void GetDefaultExportPath_without_a_directory_lands_somewhere_writable()
    {
        var path = OpmlIo.GetDefaultExportPath();

        path.Should().EndWith("podliner-feeds.opml");
        Path.IsPathRooted(path).Should().BeTrue();
    }
}
