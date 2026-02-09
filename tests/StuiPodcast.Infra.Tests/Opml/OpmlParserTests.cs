using FluentAssertions;
using StuiPodcast.Infra.Opml;
using Xunit;

namespace StuiPodcast.Infra.Tests.Opml;

public sealed class OpmlParserTests
{
    [Fact]
    public void Parses_minimal_opml()
    {
        var xml = """
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
  <head><title>t</title></head>
  <body>
    <outline text="Feed" xmlUrl="https://example.com/feed.xml" htmlUrl="https://example.com/" type="rss" />
  </body>
</opml>
""";

        var doc = OpmlParser.Parse(xml);
        doc.Count.Should().Be(1);
        doc.Entries[0].XmlUrl.Should().Be("https://example.com/feed.xml");
        doc.Entries[0].Title.Should().Be("Feed");
        doc.Entries[0].IsValid().Should().BeTrue();
    }

    [Fact]
    public void Flattens_nested_outlines_and_keeps_invalid_entries()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline text="Folder">
      <outline text="Good" xmlUrl="https://a.com/rss" />
      <outline text="Bad" xmlUrl="not-a-url" />
    </outline>
  </body>
</opml>
""";

        var doc = OpmlParser.Parse(xml);
        doc.Count.Should().Be(2);
        doc.ValidEntries().Should().HaveCount(1);
        doc.InvalidEntries().Should().HaveCount(1);
        doc.Entries.Select(e => e.Title).Should().Contain(new[] { "Good", "Bad" });
    }

    [Fact]
    public void Treats_xmlUrl_attribute_case_insensitive()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline text="X" xmlurl="https://case.example/rss" />
  </body>
</opml>
""";

        var doc = OpmlParser.Parse(xml);
        doc.Count.Should().Be(1);
        doc.Entries[0].XmlUrl.Should().Be("https://case.example/rss");
    }
}
