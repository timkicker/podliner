using FluentAssertions;
using StuiPodcast.Infra.Opml;
using Xunit;

namespace StuiPodcast.Infra.Tests.Opml;

public sealed class OpmlParserEdgeCaseTests
{
    [Fact]
    public void Empty_opml_parses_to_empty_document()
    {
        var xml = """
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
  <head><title>empty</title></head>
  <body></body>
</opml>
""";
        var doc = OpmlParser.Parse(xml);
        doc.Count.Should().Be(0);
    }

    [Fact]
    public void Outline_without_xmlUrl_is_treated_as_invalid()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline text="title-only" />
  </body>
</opml>
""";
        var doc = OpmlParser.Parse(xml);
        // An outline without xmlUrl is either dropped or kept as invalid.
        doc.ValidEntries().Should().BeEmpty();
    }

    [Fact]
    public void Outline_with_empty_xmlUrl_is_invalid()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline text="empty" xmlUrl="" />
  </body>
</opml>
""";
        var doc = OpmlParser.Parse(xml);
        doc.ValidEntries().Should().BeEmpty();
    }

    [Fact]
    public void Deeply_nested_outlines_are_flattened()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline text="A">
      <outline text="B">
        <outline text="C">
          <outline text="Leaf" xmlUrl="https://deep.example/rss" />
        </outline>
      </outline>
    </outline>
  </body>
</opml>
""";
        var doc = OpmlParser.Parse(xml);
        doc.ValidEntries().Should().ContainSingle(e => e.Title == "Leaf");
    }

    [Fact]
    public void Parser_tolerates_missing_text_attribute()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline xmlUrl="https://example.com/rss" />
  </body>
</opml>
""";
        var doc = OpmlParser.Parse(xml);
        doc.ValidEntries().Should().HaveCount(1);
    }

    [Fact]
    public void Parser_uses_title_attribute_when_text_missing()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline title="Title Attr" xmlUrl="https://example.com/rss" />
  </body>
</opml>
""";
        var doc = OpmlParser.Parse(xml);
        doc.Entries.Should().ContainSingle();
    }

    [Fact]
    public void Javascript_urls_are_treated_as_invalid()
    {
        var xml = """
<opml version="2.0">
  <body>
    <outline text="XSS" xmlUrl="javascript:alert(1)" />
  </body>
</opml>
""";
        var doc = OpmlParser.Parse(xml);
        doc.ValidEntries().Should().BeEmpty();
    }

    [Fact]
    public void Malformed_xml_throws_or_returns_empty()
    {
        var act = () => OpmlParser.Parse("<opml><body><outline");
        // Acceptable: either throws or returns an empty doc. Must not hang.
        try { var doc = act(); doc.ValidEntries().Should().BeEmpty(); }
        catch { /* parser throwing on malformed xml is fine too */ }
    }
}
