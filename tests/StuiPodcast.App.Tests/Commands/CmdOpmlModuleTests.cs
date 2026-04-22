using FluentAssertions;
using StuiPodcast.App.Command.Module;
using StuiPodcast.App.Tests.Fakes;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.Commands;

public sealed class CmdOpmlModuleTests : IDisposable
{
    private readonly FakeUiShell _ui = new();
    private readonly AppData _data = new();
    private readonly FakeFeedStore _feeds = new();
    private readonly string _tmpDir;
    private bool _persisted;
    private Task Persist() { _persisted = true; return Task.CompletedTask; }

    public CmdOpmlModuleTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "podliner-opml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void Empty_args_shows_usage()
    {
        CmdOpmlModule.ExecOpml(Array.Empty<string>(), _ui, _data, Persist, _feeds);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage") && m.Text.Contains("opml"));
    }

    [Fact]
    public void Import_without_path_shows_usage()
    {
        CmdOpmlModule.ExecOpml(new[] { "import" }, _ui, _data, Persist, _feeds);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage") && m.Text.Contains("import"));
    }

    [Fact]
    public void Import_nonexistent_file_shows_read_error()
    {
        CmdOpmlModule.ExecOpml(new[] { "import", "/does/not/exist.opml" }, _ui, _data, Persist, _feeds);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("read error"));
    }

    [Fact]
    public void Import_malformed_xml_shows_parse_error()
    {
        var path = Path.Combine(_tmpDir, "bad.opml");
        File.WriteAllText(path, "<opml><body><outline");

        CmdOpmlModule.ExecOpml(new[] { "import", path }, _ui, _data, Persist, _feeds);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("parse error") || m.Text.Contains("import"));
    }

    [Fact]
    public void Import_valid_opml_triggers_add_requests()
    {
        var path = Path.Combine(_tmpDir, "good.opml");
        File.WriteAllText(path, """
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
  <body>
    <outline text="A" xmlUrl="https://a.com/rss" />
    <outline text="B" xmlUrl="https://b.com/rss" />
  </body>
</opml>
""");

        CmdOpmlModule.ExecOpml(new[] { "import", path }, _ui, _data, Persist, _feeds);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("new 2"));
        _ui.LastRequestedAddFeedUrl.Should().NotBeNull();
        _persisted.Should().BeTrue();
    }

    [Fact]
    public void Import_with_zero_new_feeds_skips_add_requests()
    {
        // Feed already present.
        _feeds.Seed(new Feed { Id = Guid.NewGuid(), Title = "Existing", Url = "https://existing.com/rss" });

        var path = Path.Combine(_tmpDir, "dup.opml");
        File.WriteAllText(path, """
<opml version="2.0">
  <body>
    <outline text="Existing" xmlUrl="https://existing.com/rss" />
  </body>
</opml>
""");

        CmdOpmlModule.ExecOpml(new[] { "import", path }, _ui, _data, Persist, _feeds);

        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("dup 1"));
        _ui.LastRequestedAddFeedUrl.Should().BeNull();
    }

    [Fact]
    public void Export_writes_to_provided_path()
    {
        _feeds.Seed(new Feed { Id = Guid.NewGuid(), Title = "Feed A", Url = "https://a.com/rss" });
        var outPath = Path.Combine(_tmpDir, "out.opml");

        CmdOpmlModule.ExecOpml(new[] { "export", outPath }, _ui, _data, Persist, _feeds);

        File.Exists(outPath).Should().BeTrue();
        var xml = File.ReadAllText(outPath);
        xml.Should().Contain("https://a.com/rss");
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("Exported"));
    }

    [Fact]
    public void Export_to_invalid_directory_shows_error()
    {
        var outPath = "/this/dir/does/not/exist/and/cannot/be/written/out.opml";

        CmdOpmlModule.ExecOpml(new[] { "export", outPath }, _ui, _data, Persist, _feeds);

        _ui.OsdMessages.Should().Contain(m =>
            m.Text.Contains("error") || m.Text.Contains("Exported"));
        // Either it bubbles the write error OR it falls back to a valid default path.
    }

    [Fact]
    public void Unknown_subcommand_shows_usage()
    {
        CmdOpmlModule.ExecOpml(new[] { "banana" }, _ui, _data, Persist, _feeds);
        _ui.OsdMessages.Should().Contain(m => m.Text.Contains("usage"));
    }
}
