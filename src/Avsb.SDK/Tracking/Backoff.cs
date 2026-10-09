using System.Globalization;

namespace Avsb.SDK.Tracking;

/// <summary>
/// Exponential backoff with jitter, honouring a server-supplied Retry-After.
///
/// Port of reconnectBackoff.ts in @avsbhq/node, so every SDK retries on the same curve: 1s, 2s,
/// 4s, 8s, 16s, 32s, 60s (capped), with plus or minus 20 percent jitter.
///
/// The jitter is the point. A fixed retry cadence means every connected SDK hits a struggling
/// worker again at the same instant, forever, which prevents it from recovering. Jitter spreads
/// clients that started together apart on each retry.
///
/// When the server sent Retry-After (in seconds), that wins for one attempt and the attempt
/// counter still advances, so the next miss climbs the curve rather than restarting it.
/// </summary>
public static class Backoff
{
    private const long BaseDelayMs = 1_000;
    private const long MaxDelayMs = 60_000;
    private const long MinDelayMs = 500;
    private const double Jitter = 0.2;

    /// <summary>How many attempts have been made, and how long to wait before the next one.</summary>
    public readonly record struct State(int Attempt, long DelayMs);

    /// <summary>Compute the next wait. Pass a null previous state for the first retry.</summary>
    public static State Next(State? previous, long? retryAfterMs)
    {
        int attempt = (previous?.Attempt ?? 0) + 1;

        if (retryAfterMs is > 0)
        {
            return new State(attempt, retryAfterMs.Value);
        }

        long baseDelay = Math.Min(MaxDelayMs, (long)(BaseDelayMs * Math.Pow(2, attempt - 1)));
        double jitter = baseDelay * Jitter * (SharedRandom.NextDouble() * 2 - 1);
        long delay = Math.Max(MinDelayMs, (long)Math.Round(baseDelay + jitter));
        return new State(attempt, delay);
    }

    /// <summary>
    /// Parse an HTTP Retry-After header value. Only the numeric-seconds form is supported, which
    /// is what the workers emit; the HTTP-date form returns null rather than being guessed at.
    /// </summary>
    public static long? ParseRetryAfter(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        if (!double.TryParse(headerValue!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out double seconds))
        {
            return null;
        }
        if (!double.IsFinite(seconds) || seconds < 0) return null;
        return (long)Math.Round(seconds * 1000);
    }
}
