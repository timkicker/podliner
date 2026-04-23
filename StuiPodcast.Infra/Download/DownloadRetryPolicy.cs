using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace StuiPodcast.Infra.Download;

// Retry-after / backoff / transient-error policy for HTTP episode
// downloads. Extracted from DownloadManager so the retry rules can be
// unit-tested against canned exceptions/headers without starting the
// worker loop.
//
// Two knobs the caller picks per use:
//   BackoffBaseMs — first-attempt delay before exponential growth.
//   MaxBackoffMs  — ceiling so a slow server doesn't stall us for minutes.
// Retry-After from the server (delta or absolute date) takes precedence
// when present and is clamped to 120s.
internal static class DownloadRetryPolicy
{
    // Exceptions that represent a blip rather than a permanent failure.
    // Matches what a browser would silently retry under the hood.
    // For HttpRequestException we honour StatusCode (if the server actually
    // responded): 4xx other than 408/429 is permanent (404/403/410/413…) and
    // should fail fast; 5xx/408/429 and connection-level errors are transient.
    public static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode is { } code)
            {
                int n = (int)code;
                return n == 408 || n == 429 || n >= 500;
            }
            return true; // connection-level HttpRequestException (no response)
        }
        if (ex is IOException ioex && ioex.InnerException is SocketException) return true;
        if (ex is IOException iox && iox.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return true;
        if (ex is SocketException) return true;
        return false;
    }

    // Exponential backoff (base × 2^attempt) capped at maxMs, with up to
    // 150ms of jitter so parallel downloads don't retry in lockstep.
    public static int BackoffWithJitter(int attempt0, int baseMs, int maxMs = 4000)
    {
        var pow = 1 << attempt0;
        var candidate = baseMs * pow;
        var jitter = Random.Shared.Next(0, 150);
        return Math.Min(candidate + jitter, maxMs);
    }

    // Honours a Retry-After hint stashed in HttpRequestException.Data by
    // the download flow when it saw a 429/503 response; otherwise falls
    // back to the exponential-backoff schedule.
    public static int ComputeRetryDelay(Exception? last, int attempt, int baseMs, int maxMs = 4000)
    {
        if (last is HttpRequestException hre && hre.Data != null && hre.Data.Contains("RetryAfterMs"))
        {
            var v = hre.Data["RetryAfterMs"];
            if (v is int ms && ms > 0) return ms;
        }
        return BackoffWithJitter(attempt, baseMs, maxMs);
    }

    // Converts an HTTP Retry-After header (delta seconds or absolute
    // date) into a millisecond delay. Clamped to 120s to avoid stalls.
    public static int ParseRetryAfterMs(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter == null) return 0;
        if (retryAfter.Delta.HasValue)
            return (int)Math.Clamp(retryAfter.Delta.Value.TotalMilliseconds, 0, 120_000);
        if (retryAfter.Date.HasValue)
        {
            var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return (int)Math.Clamp(delta.TotalMilliseconds, 0, 120_000);
        }
        return 0;
    }
}
