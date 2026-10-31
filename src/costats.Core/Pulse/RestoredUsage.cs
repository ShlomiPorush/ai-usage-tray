namespace costats.Core.Pulse;

/// <summary>
/// Rules for usage read back from disk (the startup cache and the stored
/// Antigravity quota). Each window is judged on its own: one that has reset
/// since it was read is dropped, because its stored percentage is no longer
/// true, while the account's other windows stay.
/// </summary>
public static class RestoredUsage
{
    /// <summary>How long a window without a reset time stays usable.</summary>
    public static readonly TimeSpan MaxAgeWithoutReset = TimeSpan.FromHours(24);

    /// <summary>How far in the future a stored timestamp may lie before the record is distrusted.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// True while a window read at <paramref name="capturedAt"/> still holds:
    /// its reset is still ahead, or, without a reset time, it is at most
    /// <see cref="MaxAgeWithoutReset"/> old.
    /// </summary>
    public static bool IsCurrent(DateTimeOffset? resetsAt, DateTimeOffset capturedAt, DateTimeOffset now) =>
        resetsAt is { } reset
            ? reset > now
            : now - capturedAt <= MaxAgeWithoutReset;

    /// <summary>
    /// The state with every reading that was read back from disk removed: what
    /// the alert trackers observe, so a restored reading is never a baseline.
    /// </summary>
    public static PulseState WithoutRestoredReadings(PulseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Providers.Values.Any(reading => reading.IsRestored)
            ? state with
            {
                Providers = state.Providers
                    .Where(pair => !pair.Value.IsRestored)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            }
            : state;
    }

    /// <summary>A stored timestamp too far ahead of the clock: the record is not trusted at all.</summary>
    public static bool IsFromTheFuture(DateTimeOffset stampedAt, DateTimeOffset now) =>
        stampedAt > now + FutureTolerance;

    /// <summary>
    /// The windows of <paramref name="pulse"/> that are still current, or null
    /// when none is. Reset credits and the blocked flag are live signals and
    /// never come back from disk.
    /// </summary>
    public static UsagePulse? KeepCurrent(UsagePulse pulse, DateTimeOffset capturedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        if (IsFromTheFuture(capturedAt, now))
        {
            return null;
        }

        var session = pulse.SessionUsed is not null && IsCurrent(pulse.SessionWindow?.ResetsAt, capturedAt, now);
        var week = pulse.WeekUsed is not null && IsCurrent(pulse.WeekWindow?.ResetsAt, capturedAt, now);
        // A stored file may carry null where the record defaults to empty.
        var scoped = (pulse.ScopedQuotas ?? [])
            .Where(quota => quota is not null && IsCurrent(quota.ResetsAt, capturedAt, now))
            .ToList();

        if (!session && !week && scoped.Count == 0)
        {
            return null;
        }

        return pulse with
        {
            SessionUsed = session ? pulse.SessionUsed : null,
            SessionLimit = session ? pulse.SessionLimit : null,
            SessionWindow = session ? pulse.SessionWindow : null,
            SessionSeverity = session ? pulse.SessionSeverity : null,
            WeekUsed = week ? pulse.WeekUsed : null,
            WeekLimit = week ? pulse.WeekLimit : null,
            WeekWindow = week ? pulse.WeekWindow : null,
            WeekSeverity = week ? pulse.WeekSeverity : null,
            ScopedQuotas = scoped,
            IsBlocked = false,
            ResetCreditsAvailable = 0,
            ResetCredits = null,
            ResetCreditExpiresAt = null
        };
    }
}
