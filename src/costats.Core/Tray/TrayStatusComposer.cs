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
    DateTimeOffset? WeeklyResetsAt,
    IReadOnlyList<ScopedQuota>? ScopedQuotas = null)
{
    public static AccountUsageStatus FromUsagePulse(string label, UsagePulse usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new AccountUsageStatus(
            label,
            RemainingPercent(usage.SessionUsed, usage.SessionLimit),
            usage.SessionWindow?.ResetsAt,
            RemainingPercent(usage.WeekUsed, usage.WeekLimit),
            usage.WeekWindow?.ResetsAt,
            usage.ScopedQuotas);
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
    double? HighestUsedPercent,
    TraySeverity Severity,
    string Tooltip)
{
    /// <summary>
    /// Untruncated tooltip. <see cref="Tooltip"/> is capped at the classic
    /// shell 127-character limit; custom WPF tray tooltips can show this one.
    /// </summary>
    public string FullTooltip { get; init; } = string.Empty;
}

public static class TrayStatusComposer
{
    public const int MaximumTooltipLength = 127;

    public static TrayStatus Compose(IEnumerable<AccountUsageStatus> accounts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var materialized = accounts.ToArray();
        var usedValues = materialized
            .SelectMany(UsedPercents)
            .ToArray();

        var highest = usedValues.Length == 0 ? (double?)null : usedValues.Max();
        var severity = Classify(highest);

        var fullTooltip = string.Join('\n', materialized.Select(account => FormatAccount(account, now)));
        var tooltip = fullTooltip.Length > MaximumTooltipLength
            ? fullTooltip[..MaximumTooltipLength]
            : fullTooltip;

        return new TrayStatus(highest, severity, tooltip) { FullTooltip = fullTooltip };
    }

    /// <summary>
    /// One display row per account for rich (non-shell) tooltips: label, the
    /// formatted windows text, and the worst (highest) used percentage for colouring.
    /// </summary>
    public static IReadOnlyList<TrayAccountRow> ComposeRows(IEnumerable<AccountUsageStatus> accounts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.Select(account =>
        {
            var windows = BuildWindowTexts(account, now);
            var used = UsedPercents(account).ToArray();

            return new TrayAccountRow(
                account.Label,
                windows.Count == 0 ? "unavailable" : string.Join("  |  ", windows),
                used.Length == 0 ? null : used.Max());
        }).ToList();
    }

    /// <summary>
    /// Maps the worst used percentage to a severity. This is the exact
    /// complement of the older remaining-based rule (remaining &lt; 20 =&gt; Red,
    /// remaining &lt;= 50 =&gt; Amber).
    /// </summary>
    private static TraySeverity Classify(double? highestUsedPercent) => highestUsedPercent switch
    {
        null => TraySeverity.Unknown,
        > 80 => TraySeverity.Red,
        >= 50 => TraySeverity.Amber,
        _ => TraySeverity.Green
    };

    /// <summary>Every window's used percentage (0-100) for one account.</summary>
    private static IEnumerable<double> UsedPercents(AccountUsageStatus account)
    {
        return new[] { account.SessionRemainingPercent, account.WeeklyRemainingPercent }
            .Where(value => value.HasValue)
            .Select(value => 100 - Math.Clamp(value!.Value, 0, 100))
            .Concat((account.ScopedQuotas ?? []).Select(q => (double)Math.Clamp(q.UsedPercent, 0, 100)));
    }

    private static List<string> BuildWindowTexts(AccountUsageStatus account, DateTimeOffset now)
    {
        var windows = new List<string>(3);
        if (account.SessionRemainingPercent.HasValue && account.SessionResetsAt.HasValue)
        {
            windows.Add(FormatWindow("Session", 100 - account.SessionRemainingPercent.Value, account.SessionResetsAt.Value, now, weekly: false));
        }
        if (account.WeeklyRemainingPercent.HasValue && account.WeeklyResetsAt.HasValue)
        {
            windows.Add(FormatWindow("Weekly", 100 - account.WeeklyRemainingPercent.Value, account.WeeklyResetsAt.Value, now, weekly: true));
        }
        foreach (var scoped in account.ScopedQuotas ?? [])
        {
            var used = Math.Clamp(scoped.UsedPercent, 0, 100);
            windows.Add(scoped.ResetsAt.HasValue
                ? FormatWindow(scoped.Label, used, scoped.ResetsAt.Value, now, weekly: scoped.Group.Contains("week", StringComparison.OrdinalIgnoreCase))
                : $"{scoped.Label} {used}%");
        }

        return windows;
    }

    private static string FormatAccount(AccountUsageStatus account, DateTimeOffset now)
    {
        var windows = BuildWindowTexts(account, now);
        return windows.Count == 0
            ? $"{account.Label} unavailable"
            : $"{account.Label} {string.Join(" | ", windows)}";
    }

    /// <summary>
    /// Compact window text, e.g. "Session 86% · 1h22m". The percentage is the
    /// USED share of the quota, matching every other surface in the app.
    /// </summary>
    private static string FormatWindow(
        string label,
        double usedPercent,
        DateTimeOffset resetsAt,
        DateTimeOffset now,
        bool weekly)
    {
        var remaining = resetsAt - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var percent = Math.Clamp(usedPercent, 0, 100)
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

/// <summary>One account line for rich tray tooltips.</summary>
public sealed record TrayAccountRow(string Label, string WindowsText, double? WorstUsedPercent);
