using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Opml;
using Xunit;

namespace StuiPodcast.Infra.Tests.Opml;

public sealed class OpmlExporterTests
{
    [Fact]
    public void Export_builds_parseable_xml_with_all_urls()
    {
        var feeds = new[]
        {
            new Feed { Title = "A", Url = "https://a.example/rss" },
            new Feed { Title = "B", Url = "https://b.example/rss" }
        };

        var xml = OpmlExporter.BuildXml(feeds, "test");
        xml.Should().Contain("https://a.example/rss");
        xml.Should().Contain("https://b.example/rss");

        var doc = OpmlParser.Parse(xml);
        doc.ValidEntries().Select(e => e.XmlUrl).Should().Contain(new[]
        {
            "https://a.example/rss",
            "https://b.example/rss"
        });
    }
}
