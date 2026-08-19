using System.Globalization;
using costats.Core.Pulse;

namespace costats.Core.Tray;

public enum TraySeverity
{
    Green,
    Amber,
    Red,
    Unknown
}

public sealed record AccountUsageStatus(
    string Label,
    double? SessionRemainingPercent,
    DateTimeOffset? SessionResetsAt,
    double? WeeklyRemainingPercent,
    DateTimeOffset? WeeklyResetsAt)
{
    public static AccountUsageStatus FromUsagePulse(string label, UsagePulse usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new AccountUsageStatus(
            label,
            RemainingPercent(usage.SessionUsed, usage.SessionLimit),
            usage.SessionWindow?.ResetsAt,
            RemainingPercent(usage.WeekUsed, usage.WeekLimit),
            usage.WeekWindow?.ResetsAt);
    }

    private static double? RemainingPercent(long? used, long? limit)
    {
        if (!used.HasValue || !limit.HasValue || limit.Value <= 0)
        {
            return null;
        }

        var usedPercent = (double)used.Value / limit.Value * 100;
        return 100 - Math.Clamp(usedPercent, 0, 100);
    }
}

public sealed record TrayStatus(
    double? LowestRemainingPercent,
    TraySeverity Severity,
    string Tooltip);

public static class TrayStatusComposer
{
    public const int MaximumTooltipLength = 127;

    public static TrayStatus Compose(IEnumerable<AccountUsageStatus> accounts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var materialized = accounts.ToArray();
        var remainingValues = materialized
            .SelectMany(account => new[] { account.SessionRemainingPercent, account.WeeklyRemainingPercent })
            .Where(value => value.HasValue)
            .Select(value => Math.Clamp(value!.Value, 0, 100))
            .ToArray();

        var lowest = remainingValues.Length == 0 ? (double?)null : remainingValues.Min();
        var severity = lowest switch
        {
            null => TraySeverity.Unknown,
            < 20 => TraySeverity.Red,
            <= 50 => TraySeverity.Amber,
            _ => TraySeverity.Green
        };

        var tooltip = string.Join('\n', materialized.Select(account => FormatAccount(account, now)));
        if (tooltip.Length > MaximumTooltipLength)
        {
            tooltip = tooltip[..MaximumTooltipLength];
        }

        return new TrayStatus(lowest, severity, tooltip);
    }

    private static string FormatAccount(AccountUsageStatus account, DateTimeOffset now)
    {
        var windows = new List<string>(2);
        if (account.SessionRemainingPercent.HasValue && account.SessionResetsAt.HasValue)
        {
            windows.Add(FormatWindow("Session", account.SessionRemainingPercent.Value, account.SessionResetsAt.Value, now, weekly: false));
        }
        if (account.WeeklyRemainingPercent.HasValue && account.WeeklyResetsAt.HasValue)
        {
            windows.Add(FormatWindow("Weekly", account.WeeklyRemainingPercent.Value, account.WeeklyResetsAt.Value, now, weekly: true));
        }

        return windows.Count == 0
            ? $"{account.Label} unavailable"
            : $"{account.Label} {string.Join(" | ", windows)}";
    }

    private static string FormatWindow(
        string label,
        double remainingPercent,
        DateTimeOffset resetsAt,
        DateTimeOffset now,
        bool weekly)
    {
        var remaining = resetsAt - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var percent = Math.Clamp(remainingPercent, 0, 100)
            .ToString("0", CultureInfo.InvariantCulture);

        if (weekly)
        {
            var days = remaining.TotalDays.ToString("0.0", CultureInfo.InvariantCulture);
            return $"{label} {percent}% · {days}d";
        }

        var totalHours = (int)Math.Floor(remaining.TotalHours);
        return $"{label} {percent}% · {totalHours}h{remaining.Minutes:00}m";
    }
}
