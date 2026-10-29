namespace WebhookApi.Services;

public static class RetryPolicy
{
    public const int MaxAttempts = 4;

    /// <summary>
    /// Delay before the next attempt after the given (1-based) failed attempt:
    /// +1 min, +5 min, +30 min. Null once attempts are exhausted.
    /// </summary>
    public static TimeSpan? DelayAfterFailedAttempt(int attemptCount) => attemptCount switch
    {
        1 => TimeSpan.FromSeconds(60),
        2 => TimeSpan.FromSeconds(300),
        3 => TimeSpan.FromSeconds(1800),
        _ => null,
    };
}
