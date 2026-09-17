namespace costats.Core.Pulse;

public static class ResetCreditExpiry
{
    public static int DaysLeft(DateTimeOffset expiresAt, DateTimeOffset now) =>
        (expiresAt.ToLocalTime().Date - now.ToLocalTime().Date).Days;

    public static string RemainingText(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (expiresAt <= now) return "Expired";
        var days = DaysLeft(expiresAt, now);
        return days == 0 ? "0 days left, expires today" : days == 1 ? "1 day left" : $"{days} days left";
    }

    public static string Notice(IReadOnlyList<ResetCredit>? credits, DateTimeOffset? fallback, DateTimeOffset now)
    {
        bool IsSoon(DateTimeOffset? date) => date is { } expiry && expiry > now && DaysLeft(expiry, now) <= 7;
        var count = credits is null ? (IsSoon(fallback) ? 1 : 0) : credits.Count(credit => IsSoon(credit.ExpiresAt));
        return count == 0 ? string.Empty : count == 1 ? "1 reset expires within 7 days." : $"{count} resets expire within 7 days.";
    }
}
