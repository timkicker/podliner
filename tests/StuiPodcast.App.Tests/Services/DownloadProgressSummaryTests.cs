using FluentAssertions;
using StuiPodcast.App.Services;
using StuiPodcast.Core;
using Xunit;
using Item = StuiPodcast.App.Services.DownloadProgressSummary.Item;

namespace StuiPodcast.App.Tests.Services;

// The figure in the window badge while downloads run. Weighted by bytes, not
// by item count, so a 500 MB episode does not read the same as a 5 MB one.
public sealed class DownloadProgressSummaryTests
{
    private const long MB = 1024L * 1024L;

    private static Item Running(long got, long total) => new(got, total, DownloadState.Running);
    private static Item Done(long total)              => new(total, total, DownloadState.Done);
    private static Item Queued(long? total = null)    => new(0, total, DownloadState.Queued);

    // ── empty and trivial ───────────────────────────────────────────────────

    [Fact]
    public void Nothing_queued_is_zero_percent()
        => DownloadProgressSummary.Percent(Array.Empty<Item>()).Should().Be(0);

    [Fact]
    public void A_single_finished_item_is_a_hundred()
        => DownloadProgressSummary.Percent(new[] { Done(10 * MB) }).Should().Be(100);

    [Fact]
    public void A_single_untouched_item_is_zero()
        => DownloadProgressSummary.Percent(new[] { Running(0, 10 * MB) }).Should().Be(0);

    [Fact]
    public void A_half_finished_item_is_fifty()
        => DownloadProgressSummary.Percent(new[] { Running(5 * MB, 10 * MB) }).Should().Be(50);

    // ── byte weighting ──────────────────────────────────────────────────────

    [Fact]
    public void Progress_is_weighted_by_bytes_not_by_item_count()
    {
        // One finished 1 MB file next to an untouched 99 MB file is 1%, not
        // the 50% an item-count average would report.
        var pct = DownloadProgressSummary.Percent(new[]
        {
            Done(1 * MB),
            Running(0, 99 * MB),
        });

        pct.Should().Be(1);
    }

    [Fact]
    public void A_big_file_dominates_the_figure()
    {
        var pct = DownloadProgressSummary.Percent(new[]
        {
            Done(1 * MB),
            Running(50 * MB, 100 * MB),
        });

        // 51 of 101 MB
        pct.Should().Be(50);
    }

    [Fact]
    public void Verifying_counts_like_running()
    {
        var pct = DownloadProgressSummary.Percent(new[]
        {
            new Item(8 * MB, 10 * MB, DownloadState.Verifying),
        });

        pct.Should().Be(80);
    }

    // ── items that carry no weight ──────────────────────────────────────────

    [Fact]
    public void Items_without_a_known_size_do_not_skew_the_figure()
    {
        var pct = DownloadProgressSummary.Percent(new[]
        {
            Running(5 * MB, 10 * MB),
            Queued(),               // server never announced a length
        });

        pct.Should().Be(50);
    }

    [Fact]
    public void A_zero_length_total_is_ignored()
    {
        var pct = DownloadProgressSummary.Percent(new[]
        {
            Running(5 * MB, 10 * MB),
            Running(0, 0),
        });

        pct.Should().Be(50);
    }

    [Fact]
    public void Queued_items_with_a_known_size_do_not_count_yet()
    {
        // Queued is not started, so its bytes belong in neither sum;
        // otherwise adding to the queue would drop the percentage.
        var pct = DownloadProgressSummary.Percent(new[]
        {
            Done(10 * MB),
            Queued(90 * MB),
        });

        pct.Should().Be(100);
    }

    // ── fallback when no size is known at all ───────────────────────────────

    [Fact]
    public void With_no_known_sizes_it_counts_finished_items()
    {
        var pct = DownloadProgressSummary.Percent(new[]
        {
            new Item(0, null, DownloadState.Done),
            new Item(0, null, DownloadState.Running),
            new Item(0, null, DownloadState.Running),
            new Item(0, null, DownloadState.Running),
        });

        pct.Should().Be(25);
    }

    [Fact]
    public void With_no_known_sizes_and_nothing_finished_it_is_zero()
    {
        var pct = DownloadProgressSummary.Percent(new[]
        {
            new Item(0, null, DownloadState.Running),
            new Item(0, null, DownloadState.Queued),
        });

        pct.Should().Be(0);
    }

    // ── robustness ──────────────────────────────────────────────────────────

    [Fact]
    public void Received_bytes_beyond_the_total_cannot_push_it_past_a_hundred()
    {
        // Servers do lie about Content-Length.
        var pct = DownloadProgressSummary.Percent(new[] { Running(50 * MB, 10 * MB) });

        pct.Should().Be(100);
    }

    [Fact]
    public void Negative_received_bytes_are_treated_as_zero()
        => DownloadProgressSummary.Percent(new[] { Running(-500, 10 * MB) }).Should().Be(0);

    [Fact]
    public void The_result_always_lands_between_zero_and_a_hundred()
    {
        var cases = new[]
        {
            new[] { Running(0, 1) },
            new[] { Running(long.MaxValue / 2, 1) },
            new[] { Done(1), Running(0, long.MaxValue / 4) },
        };

        foreach (var c in cases)
            DownloadProgressSummary.Percent(c).Should().BeInRange(0, 100);
    }

    [Fact]
    public void Failed_and_canceled_items_carry_no_weight()
    {
        var pct = DownloadProgressSummary.Percent(new[]
        {
            Done(10 * MB),
            new Item(3 * MB, 10 * MB, DownloadState.Failed),
            new Item(1 * MB, 10 * MB, DownloadState.Canceled),
        });

        pct.Should().Be(100);
    }
}
