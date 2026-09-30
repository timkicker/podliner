using FluentAssertions;
using Podliner.App.Bootstrap;
using Podliner.App.Tests.Fakes;
using Podliner.Core;
using Xunit;

namespace Podliner.App.Tests.Bootstrap;

// Homebrew could not package podliner: its formula test needs a command that
// does something real without a terminal and exits, and --opml-import and
// --opml-export only ran once the TUI was up. Without a terminal they now do
// their work and exit, before an audio engine is even looked for.
public sealed class HeadlessOpmlTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("podliner-headless-opml-").FullName;
    readonly FakeFeedStore _feeds = new();
    readonly FakeEpisodeStore _episodes = new();
    readonly AppData _data = new();
    readonly StringWriter _out = new();
    readonly StringWriter _err = new();
    int _saves;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Opml(params (string title, string url)[] feeds)
    {
        var path = Path.Combine(_dir, $"in-{Guid.NewGuid():N}.opml");
        File.WriteAllText(path,
            "<?xml version=\"1.0\"?><opml version=\"2.0\"><head><title>t</title></head><body>" +
            string.Concat(feeds.Select(f => $"<outline text=\"{f.title}\" type=\"rss\" xmlUrl=\"{f.url}\"/>")) +
            "</body></opml>");
        return path;
    }

    int Run(string? import = null, string? mode = null, string? export = null)
        => HeadlessOpml.Run(
            new CliEntrypoint.Options { OpmlImport = import, OpmlImportMode = mode, OpmlExport = export },
            _feeds, _episodes, _data, () => _saves++, _out, _err);

    [Fact]
    public void Wants_only_the_opml_flags()
    {
        HeadlessOpml.Wants(new CliEntrypoint.Options()).Should().BeFalse();
        HeadlessOpml.Wants(new CliEntrypoint.Options { OpmlExport = "x" }).Should().BeTrue();
        HeadlessOpml.Wants(new CliEntrypoint.Options { OpmlImport = "x" }).Should().BeTrue();
    }

    [Fact]
    public void Export_writes_every_feed_and_exits_0()
    {
        _feeds.Seed(new Feed { Title = "Alpha", Url = "https://ex.test/a.xml" },
                    new Feed { Title = "Beta",  Url = "https://ex.test/b.xml" });
        var path = Path.Combine(_dir, "out.opml");

        Run(export: path).Should().Be(0);

        var xml = File.ReadAllText(path);
        xml.Should().Contain("https://ex.test/a.xml").And.Contain("https://ex.test/b.xml");
        _out.ToString().Should().Contain("2 feeds").And.Contain(path);
    }

    [Fact]
    public void Import_adds_the_feeds_without_touching_the_network_and_saves()
    {
        _feeds.Seed(new Feed { Title = "Alpha", Url = "https://ex.test/a.xml" });
        _data.LastRefreshAt = DateTimeOffset.Now;

        Run(import: Opml(("Alpha", "https://ex.test/a.xml"), ("Beta", "https://ex.test/b.xml"))).Should().Be(0);

        _feeds.Snapshot().Select(f => f.Url).Should().BeEquivalentTo("https://ex.test/a.xml", "https://ex.test/b.xml");
        _feeds.Snapshot().Single(f => f.Url.EndsWith("b.xml")).Title.Should().Be("Beta");
        _saves.Should().Be(1);
        _data.LastRefreshAt.Should().BeNull("the new feeds have no episodes yet, the next start should fetch them");
        _out.ToString().Should().Contain("1 new").And.Contain("1 already");
    }

    [Fact]
    public void Import_then_export_in_one_run_round_trips()
    {
        var export = Path.Combine(_dir, "out.opml");

        Run(import: Opml(("Beta", "https://ex.test/b.xml")), export: export).Should().Be(0);

        File.ReadAllText(export).Should().Contain("https://ex.test/b.xml");
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        Run(import: Opml(("Beta", "https://ex.test/b.xml")), mode: "dry-run").Should().Be(0);

        _feeds.Snapshot().Should().BeEmpty();
        _saves.Should().Be(0);
        _out.ToString().Should().Contain("1 new");
    }

    [Fact]
    public void Replace_drops_the_old_feeds_and_their_episodes()
    {
        var old = new Feed { Title = "Old", Url = "https://ex.test/old.xml" };
        _feeds.Seed(old);
        _episodes.Seed(new Episode { FeedId = old.Id, Title = "e", AudioUrl = "https://ex.test/e.mp3" });

        Run(import: Opml(("Beta", "https://ex.test/b.xml")), mode: "replace").Should().Be(0);

        _feeds.Snapshot().Select(f => f.Url).Should().Equal("https://ex.test/b.xml");
        _episodes.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void A_missing_file_is_an_error_with_exit_1()
    {
        Run(import: Path.Combine(_dir, "nope.opml")).Should().Be(1);

        _err.ToString().Should().Contain("nope.opml");
        _saves.Should().Be(0);
    }
}
