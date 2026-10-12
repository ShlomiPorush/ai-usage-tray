namespace costats.Application.Diagnostics;

/// <summary>
/// When debug logging must switch itself off. Pure: the clock is passed in.
/// </summary>
public static class DebugLoggingPolicy
{
    /// <summary>How long debug logging stays on after it was turned on.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Slack for a clock that was set back a little after debug logging was
    /// turned on. A start time further in the future than this cannot be
    /// trusted to ever expire, so it counts as expired.
    /// </summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>
    /// True when debug logging is on and must be turned off now: its lifetime
    /// has passed, or its start time is missing or not believable.
    /// </summary>
    public static bool IsExpired(bool enabled, DateTimeOffset? enabledAt, DateTimeOffset now)
    {
        if (!enabled)
        {
            return false;
        }

        if (enabledAt is not { } start)
        {
            return true;
        }

        return now - start >= Lifetime || start - now > FutureTolerance;
    }

    /// <summary>When debug logging that started at <paramref name="enabledAt"/> turns off.</summary>
    public static DateTimeOffset ExpiresAt(DateTimeOffset enabledAt) => enabledAt + Lifetime;
}
