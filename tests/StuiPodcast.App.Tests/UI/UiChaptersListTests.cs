using FluentAssertions;
using StuiPodcast.App.UI.Controls;
using StuiPodcast.Core;
using Xunit;

namespace StuiPodcast.App.Tests.UI;

public sealed class UiChaptersListTests
{
    // ── IndexForPosition ─────────────────────────────────────────────────────

    [Fact]
    public void IndexForPosition_empty_returns_negative_one()
    {
        UiChaptersList.IndexForPosition(Array.Empty<Chapter>(), 0).Should().Be(-1);
    }

    [Fact]
    public void IndexForPosition_before_first_chapter_returns_negative_one()
    {
        var chapters = new[]
        {
            new Chapter { StartSeconds = 10, Title = "A" },
            new Chapter { StartSeconds = 20, Title = "B" },
        };
        UiChaptersList.IndexForPosition(chapters, 5).Should().Be(-1);
    }

    [Fact]
    public void IndexForPosition_at_chapter_start_returns_that_index()
    {
        var chapters = new[]
        {
            new Chapter { StartSeconds = 0,  Title = "A" },
            new Chapter { StartSeconds = 10, Title = "B" },
            new Chapter { StartSeconds = 20, Title = "C" },
        };
        UiChaptersList.IndexForPosition(chapters, 10).Should().Be(1);
        UiChaptersList.IndexForPosition(chapters, 20).Should().Be(2);
    }

    [Fact]
    public void IndexForPosition_between_chapters_returns_lower_index()
    {
        var chapters = new[]
        {
            new Chapter { StartSeconds = 0,  Title = "A" },
            new Chapter { StartSeconds = 10, Title = "B" },
            new Chapter { StartSeconds = 20, Title = "C" },
        };
        UiChaptersList.IndexForPosition(chapters, 15).Should().Be(1);
    }

    [Fact]
    public void IndexForPosition_past_last_chapter_returns_last()
    {
        var chapters = new[]
        {
            new Chapter { StartSeconds = 0,  Title = "A" },
            new Chapter { StartSeconds = 10, Title = "B" },
        };
        UiChaptersList.IndexForPosition(chapters, 9999).Should().Be(1);
    }

    // Out-of-order chapters shouldn't happen because the fetcher sorts; if
    // somehow they do, the scan is still well-behaved (sorted-assumption
    // search just stops early, which is acceptable).

    // ── FormatTime ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,    "0:00")]
    [InlineData(5,    "0:05")]
    [InlineData(59,   "0:59")]
    [InlineData(60,   "1:00")]
    [InlineData(90,   "1:30")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(36000,"10:00:00")]
    public void FormatTime_matches_expected(double sec, string expected)
    {
        UiChaptersList.FormatTime(sec).Should().Be(expected);
    }

    [Fact]
    public void FormatTime_nan_or_negative_is_zero()
    {
        UiChaptersList.FormatTime(double.NaN).Should().Be("0:00");
        UiChaptersList.FormatTime(-5).Should().Be("0:00");
    }
}
