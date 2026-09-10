using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FluentAssertions;
using Podliner.Infra.Download;
using Xunit;

namespace Podliner.Infra.Tests.Download;

public sealed class DownloadRetryPolicyTests
{
    // ── IsTransient ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]        // 408
    [InlineData(HttpStatusCode.TooManyRequests)]       // 429
    [InlineData(HttpStatusCode.InternalServerError)]   // 500
    [InlineData(HttpStatusCode.BadGateway)]            // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]    // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]        // 504
    public void IsTransient_true_for_retryable_status_codes(HttpStatusCode code)
        => DownloadRetryPolicy.IsTransient(new HttpRequestException("boom", null, code))
            .Should().BeTrue();

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]           // 404
    [InlineData(HttpStatusCode.Forbidden)]          // 403
    [InlineData(HttpStatusCode.Unauthorized)]       // 401
    [InlineData(HttpStatusCode.Gone)]               // 410
    [InlineData(HttpStatusCode.BadRequest)]         // 400
    [InlineData(HttpStatusCode.RequestEntityTooLarge)] // 413
    public void IsTransient_false_for_permanent_client_errors(HttpStatusCode code)
        => DownloadRetryPolicy.IsTransient(new HttpRequestException("boom", null, code))
            .Should().BeFalse();

    [Fact]
    public void IsTransient_true_when_the_server_never_responded()
    {
        // No StatusCode means the request died at the connection level.
        DownloadRetryPolicy.IsTransient(new HttpRequestException("connection refused"))
            .Should().BeTrue();
    }

    [Fact]
    public void IsTransient_true_for_socket_errors()
        => DownloadRetryPolicy.IsTransient(new SocketException((int)SocketError.ConnectionReset))
            .Should().BeTrue();

    [Fact]
    public void IsTransient_true_for_io_wrapping_a_socket_error()
        => DownloadRetryPolicy.IsTransient(new IOException("read failed", new SocketException(110)))
            .Should().BeTrue();

    [Fact]
    public void IsTransient_true_for_a_timed_out_io_error()
        => DownloadRetryPolicy.IsTransient(new IOException("The operation timed out."))
            .Should().BeTrue();

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public void IsTransient_false_for_unrelated_exceptions(Type exType)
        => DownloadRetryPolicy.IsTransient((Exception)Activator.CreateInstance(exType)!)
            .Should().BeFalse();

    [Fact]
    public void IsTransient_false_for_a_plain_io_error()
        => DownloadRetryPolicy.IsTransient(new IOException("disk full")).Should().BeFalse();

    // ── BackoffWithJitter ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 400, 400, 550)]    // 400 * 1
    [InlineData(1, 400, 800, 950)]    // 400 * 2
    [InlineData(2, 400, 1600, 1750)]  // 400 * 4
    [InlineData(3, 400, 3200, 3350)]
    public void BackoffWithJitter_grows_exponentially_within_the_jitter_window(
        int attempt, int baseMs, int lo, int hi)
    {
        var delay = DownloadRetryPolicy.BackoffWithJitter(attempt, baseMs, maxMs: 100_000);
        delay.Should().BeInRange(lo, hi);
    }

    [Fact]
    public void BackoffWithJitter_never_exceeds_the_ceiling()
    {
        for (int attempt = 0; attempt < 12; attempt++)
            DownloadRetryPolicy.BackoffWithJitter(attempt, 400, maxMs: 4000)
                .Should().BeLessThanOrEqualTo(4000);
    }

    [Fact]
    public void BackoffWithJitter_is_not_always_the_same_value()
    {
        // Jitter exists so parallel downloads don't retry in lockstep.
        var seen = new HashSet<int>();
        for (int i = 0; i < 60; i++)
            seen.Add(DownloadRetryPolicy.BackoffWithJitter(0, 400, maxMs: 100_000));

        seen.Should().HaveCountGreaterThan(1);
    }

    // ── ComputeRetryDelay ───────────────────────────────────────────────────

    [Fact]
    public void ComputeRetryDelay_prefers_a_server_supplied_hint()
    {
        var ex = new HttpRequestException("429");
        ex.Data["RetryAfterMs"] = 7_000;

        DownloadRetryPolicy.ComputeRetryDelay(ex, attempt: 0, baseMs: 400).Should().Be(7_000);
    }

    [Fact]
    public void ComputeRetryDelay_ignores_a_non_positive_hint()
    {
        var ex = new HttpRequestException("429");
        ex.Data["RetryAfterMs"] = 0;

        DownloadRetryPolicy.ComputeRetryDelay(ex, attempt: 0, baseMs: 400, maxMs: 100_000)
            .Should().BeInRange(400, 550);
    }

    [Fact]
    public void ComputeRetryDelay_falls_back_to_backoff_without_an_exception()
        => DownloadRetryPolicy.ComputeRetryDelay(null, attempt: 1, baseMs: 400, maxMs: 100_000)
            .Should().BeInRange(800, 950);

    [Fact]
    public void ComputeRetryDelay_falls_back_to_backoff_for_other_exceptions()
        => DownloadRetryPolicy.ComputeRetryDelay(new SocketException(110), attempt: 0, baseMs: 400, maxMs: 100_000)
            .Should().BeInRange(400, 550);

    // ── ParseRetryAfterMs ───────────────────────────────────────────────────

    [Fact]
    public void ParseRetryAfterMs_reads_a_delta_in_seconds()
        => DownloadRetryPolicy.ParseRetryAfterMs(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)))
            .Should().Be(30_000);

    [Fact]
    public void ParseRetryAfterMs_clamps_a_long_delta_to_120s()
        => DownloadRetryPolicy.ParseRetryAfterMs(new RetryConditionHeaderValue(TimeSpan.FromHours(1)))
            .Should().Be(120_000);

    [Fact]
    public void ParseRetryAfterMs_reads_an_absolute_date()
    {
        var when = DateTimeOffset.UtcNow.AddSeconds(20);

        DownloadRetryPolicy.ParseRetryAfterMs(new RetryConditionHeaderValue(when))
            .Should().BeInRange(17_000, 20_000);
    }

    [Fact]
    public void ParseRetryAfterMs_floors_a_date_in_the_past_at_zero()
        => DownloadRetryPolicy.ParseRetryAfterMs(
                new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5)))
            .Should().Be(0);

    [Fact]
    public void ParseRetryAfterMs_returns_zero_when_the_header_is_missing()
        => DownloadRetryPolicy.ParseRetryAfterMs(null).Should().Be(0);
}
