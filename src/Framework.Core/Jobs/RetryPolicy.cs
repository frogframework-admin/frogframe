namespace Framework.Jobs;

/// <summary>
/// Standardized exponential backoff retry policy for background integration workers and delivery queues.
/// Matches OutboxProcessor retry behavior: MaxAttempts = 5 with exponential intervals (2^n seconds).
/// </summary>
public static class RetryPolicy
{
    public const int MaxAttempts = 5;

    /// <summary>
    /// Calculates exponential backoff delay based on the attempt count (2^attempt seconds).
    /// Attempt 1: 2s, Attempt 2: 4s, Attempt 3: 8s, Attempt 4: 16s, Attempt 5: 32s.
    /// </summary>
    public static TimeSpan GetBackoff(int attempt)
    {
        if (attempt <= 0) return TimeSpan.FromSeconds(1);
        var seconds = Math.Min(300, Math.Pow(2, attempt)); // capped at 5 minutes
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Calculates the next execution timestamp for a failed task.
    /// </summary>
    public static DateTimeOffset CalculateNextAttempt(int attempt, DateTimeOffset? now = null)
    {
        var baseTime = now ?? DateTimeOffset.UtcNow;
        return baseTime.Add(GetBackoff(attempt));
    }

    /// <summary>
    /// Determines whether the task should be retried or transitioned to dead-letter state.
    /// </summary>
    public static bool ShouldRetry(int attempt) => attempt < MaxAttempts;
}

