namespace SwiftRide.TripService.Application.Reliability;

public static class RetrySchedule
{
    public static TimeSpan NextDelay(
        int attempt,
        TimeSpan initial,
        TimeSpan max,
        double jitterFraction,
        TimeSpan? retryAfter = null,
        DateTimeOffset? deadlineUtc = null,
        DateTimeOffset? nowUtc = null)
    {
        if (initial <= TimeSpan.Zero || max <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var clampedAttempt = Math.Max(1, attempt);
        var clampedJitter = Math.Clamp(jitterFraction, 0.0, 1.0);

        // base = min(maxDelay, initialDelay * 2^(attempt-1))
        var multiplier = Math.Pow(2, Math.Min(30, clampedAttempt - 1));
        var baseSeconds = Math.Min(max.TotalSeconds, initial.TotalSeconds * multiplier);

        // jitter: cộng trong khoảng 0..25%
        var jitterSeconds = baseSeconds * (clampedJitter * 0.25);
        var totalSeconds = Math.Min(max.TotalSeconds, baseSeconds + jitterSeconds);

        var delay = TimeSpan.FromSeconds(totalSeconds);

        // Tôn trọng Retry-After nếu có và lớn hơn delay tính toán
        if (retryAfter.HasValue && retryAfter.Value > delay)
        {
            delay = retryAfter.Value;
        }

        // Không vượt quá deadline
        if (deadlineUtc.HasValue && nowUtc.HasValue)
        {
            var remaining = deadlineUtc.Value - nowUtc.Value;
            if (remaining <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }
            if (delay > remaining)
            {
                delay = remaining;
            }
        }

        return delay;
    }

    public static TimeSpan NextDelay(int attempt, TimeSpan initial, TimeSpan max, Random? random = null)
    {
        var jitter = (random ?? Random.Shared).NextDouble();
        return NextDelay(attempt, initial, max, jitter);
    }
}
