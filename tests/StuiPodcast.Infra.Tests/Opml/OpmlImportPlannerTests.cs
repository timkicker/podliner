using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Opml;
using Xunit;

namespace StuiPodcast.Infra.Tests.Opml;

public sealed class OpmlImportPlannerTests
{
    [Fact]
    public void Detects_new_duplicate_and_invalid()
    {
        var existing = new List<Feed>
        {
            new Feed { Title = "Old", Url = "https://example.com/feed.xml" }
        };

        var xml = """
<opml version="2.0">
  <body>
    <outline text="Dup" xmlUrl="https://example.com/feed.xml" />
    <outline text="New" xmlUrl="https://new.example/feed" />
    <outline text="Bad" xmlUrl="not-a-url" />
  </body>
</opml>
""";

        var doc = OpmlParser.Parse(xml);
        var plan = OpmlImportPlanner.Plan(doc, existing, updateTitles: false);

        plan.NewItems().Should().HaveCount(1);
        plan.DuplicateItems().Should().HaveCount(1);
        plan.InvalidItems().Should().HaveCount(1);

        plan.NewItems().Single().Entry.XmlUrl.Should().Be("https://new.example/feed");
    }
}
