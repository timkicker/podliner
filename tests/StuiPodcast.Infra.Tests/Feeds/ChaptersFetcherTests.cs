using FluentAssertions;
using StuiPodcast.Infra.Feeds;
using Xunit;

namespace StuiPodcast.Infra.Tests.Feeds;

public sealed class ChaptersFetcherTests
{
    [Fact]
    public void Parse_returns_empty_list_for_zero_chapters()
    {
        var json = """{"version":"1.2.0","chapters":[]}""";
        var result = ChaptersFetcher.Parse(json);
        result.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Parse_extracts_required_fields()
    {
        var json = """
            {
              "version":"1.2.0",
              "chapters":[
                {"startTime":0, "title":"Intro"},
                {"startTime":120, "title":"Main", "url":"https://ex/main", "img":"https://ex/img.jpg"},
                {"startTime":600, "title":"Outro"}
              ]
            }
            """;

        var result = ChaptersFetcher.Parse(json);
        result.Should().NotBeNull();
        result!.Should().HaveCount(3);
        result[0].StartSeconds.Should().Be(0);
        result[0].Title.Should().Be("Intro");
        result[1].Url.Should().Be("https://ex/main");
        result[1].Img.Should().Be("https://ex/img.jpg");
    }

    [Fact]
    public void Parse_sorts_by_startTime()
    {
        var json = """
            {"chapters":[
                {"startTime":600,"title":"Third"},
                {"startTime":0,"title":"First"},
                {"startTime":60,"title":"Second"}
            ]}
            """;
        var result = ChaptersFetcher.Parse(json);
        result!.Select(c => c.Title).Should().Equal("First", "Second", "Third");
    }

    [Fact]
    public void Parse_skips_chapters_with_negative_or_missing_startTime()
    {
        var json = """
            {"chapters":[
                {"startTime":-5,"title":"Bad"},
                {"title":"NoStart"},
                {"startTime":10,"title":"Good"}
            ]}
            """;
        var result = ChaptersFetcher.Parse(json);
        result!.Should().ContainSingle(c => c.Title == "Good");
    }

    [Fact]
    public void Parse_accepts_string_startTime_numbers()
    {
        var json = """{"chapters":[{"startTime":"42.5","title":"x"}]}""";
        var result = ChaptersFetcher.Parse(json);
        result!.Single().StartSeconds.Should().Be(42.5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]                                   // top-level isn't object with "chapters"
    [InlineData("{\"chapters\":\"oops\"}")]             // chapters must be array
    public void Parse_returns_null_for_unusable_input(string json)
    {
        ChaptersFetcher.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_empty_title_yields_empty_string()
    {
        var json = """{"chapters":[{"startTime":0}]}""";
        var result = ChaptersFetcher.Parse(json);
        result!.Single().Title.Should().BeEmpty();
    }
}
