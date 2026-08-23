using System.Globalization;
using costats.Core.Pulse;

namespace costats.Core.Tray;

/// <summary>
/// The four used-percent bands (see <see cref="UsageBands"/>) plus "no data".
/// </summary>
public enum TraySeverity
{
    Green,
    Yellow,
    Orange,
    Red,
    Unknown
}

public sealed record AccountUsageStatus(
    string Label,
    double? SessionRemainingPercent,
    DateTimeOffset? SessionResetsAt,
    double? WeeklyRemainingPercent,
    DateTimeOffset? WeeklyResetsAt,
    IReadOnlyList<ScopedQuota>? ScopedQuotas = null,
    // Carried so the remote payload can still report what the provider said.
    // Colours and status wording come from the used number alone.
    QuotaSeverity? SessionSeverity = null,
    QuotaSeverity? WeeklySeverity = null,
    bool IsBlocked = false)
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
            usage.ScopedQuotas,
            usage.SessionSeverity,
            usage.WeekSeverity,
            usage.IsBlocked);
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

    /// <summary>
    /// Compact two-row text for the draggable clock panel. It keeps the
    /// reference panel's PA/Claude and GPT/GLM grouping when those labels are
    /// present, then fills any remaining slots in account order.
    /// </summary>
    public string PanelText { get; init; } = string.Empty;

    /// <summary>The number drawn on the tray icon in the selected display mode.</summary>
    public double? DisplayPercent { get; init; }
}

public static class TrayStatusComposer
{
    public const int MaximumTooltipLength = 127;

    public static TrayStatus Compose(
        IEnumerable<AccountUsageStatus> accounts,
        DateTimeOffset now,
        bool showPercentageLeft = false)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var materialized = accounts.ToArray();
        var usedValues = materialized
            .SelectMany(UsedPercents)
            .ToArray();

        var highest = usedValues.Length == 0 ? (double?)null : usedValues.Max();
        var severity = ComposeSeverity(materialized);

        var fullTooltip = string.Join('\n', materialized.Select(account =>
            FormatAccount(account, now, showPercentageLeft)));
        var tooltip = fullTooltip.Length > MaximumTooltipLength
            ? fullTooltip[..MaximumTooltipLength]
            : fullTooltip;

        return new TrayStatus(highest, severity, tooltip)
        {
            FullTooltip = fullTooltip,
            PanelText = BuildPanelText(materialized, now, showPercentageLeft),
            DisplayPercent = highest.HasValue
                ? UsagePercentageDisplay.Value(highest.Value, showPercentageLeft)
                : null
        };
    }

    private static string BuildPanelText(
        IReadOnlyList<AccountUsageStatus> accounts,
        DateTimeOffset now,
        bool showPercentageLeft)
    {
        var remaining = accounts.ToList();
        var ordered = new List<AccountUsageStatus>(Math.Min(accounts.Count, 4));

        foreach (var label in new[] { "PA", "Claude", "GPT", "GLM" })
        {
            var match = remaining.FirstOrDefault(account =>
                account.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                continue;
            }

            ordered.Add(match);
            remaining.Remove(match);
        }

        ordered.AddRange(remaining);

        return string.Join('\n', ordered
            .Take(4)
            .Chunk(2)
            .Select(pair => string.Join(" | ", pair.Select(account =>
                FormatPanelAccount(account, now, showPercentageLeft)))));
    }

    private static string FormatPanelAccount(
        AccountUsageStatus account,
        DateTimeOffset now,
        bool showPercentageLeft)
    {
        var windows = new List<string>(2);
        if (account.WeeklyRemainingPercent is { } weeklyRemaining)
        {
            windows.Add(FormatPanelWindow(
                "W", 100 - weeklyRemaining, account.WeeklyResetsAt, now, weekly: true, showPercentageLeft));
        }
        if (account.SessionRemainingPercent is { } sessionRemaining)
        {
            windows.Add(FormatPanelWindow(
                "S", 100 - sessionRemaining, account.SessionResetsAt, now, weekly: false, showPercentageLeft));
        }

        return windows.Count == 0
            ? $"{account.Label}: unavailable"
            : $"{account.Label}: {string.Join(" <> ", windows)}";
    }

    private static string FormatPanelWindow(
        string label,
        double usedPercent,
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        bool weekly,
        bool showPercentageLeft)
    {
        var percent = UsagePercentageDisplay.Value(usedPercent, showPercentageLeft)
            .ToString("0", CultureInfo.InvariantCulture);
        if (!resetsAt.HasValue)
        {
            return $"{label} {percent}%";
        }

        var remaining = resetsAt.Value - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        if (weekly)
        {
            return $"{label} {percent}%-{remaining.TotalDays.ToString("0.0", CultureInfo.InvariantCulture)}d";
        }

        var totalHours = (int)Math.Floor(remaining.TotalHours);
        return $"{label} {percent}%-{totalHours}h{remaining.Minutes:00}m";
    }

    /// <summary>
    /// One display row per account for rich (non-shell) tooltips: label, the
    /// formatted windows text, and the worst (highest) used percentage for colouring.
    /// </summary>
    public static IReadOnlyList<TrayAccountRow> ComposeRows(
        IEnumerable<AccountUsageStatus> accounts,
        DateTimeOffset now,
        bool showPercentageLeft = false)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.Select(account =>
        {
            var windows = BuildWindowTexts(account, now, showPercentageLeft);
            var used = UsedPercents(account).ToArray();

            return new TrayAccountRow(
                account.Label,
                windows.Count == 0 ? "unavailable" : string.Join("  |  ", windows),
                used.Length == 0 ? null : used.Max());
        }).ToList();
    }

    /// <summary>
    /// Maps a used percentage to its band: green 0-49, yellow 50-74,
    /// orange 75-89, red 90-100.
    /// </summary>
    private static TraySeverity Classify(double? highestUsedPercent) => highestUsedPercent switch
    {
        null => TraySeverity.Unknown,
        { } used => UsageBands.Of(used) switch
        {
            UsageBand.Red => TraySeverity.Red,
            UsageBand.Orange => TraySeverity.Orange,
            UsageBand.Yellow => TraySeverity.Yellow,
            _ => TraySeverity.Green
        }
    };

    /// <summary>
    /// Worst band across every window of every account. The used number alone
    /// decides; a provider's own severity rating never overrides it.
    /// </summary>
    private static TraySeverity ComposeSeverity(IReadOnlyList<AccountUsageStatus> accounts)
    {
        var worst = TraySeverity.Unknown;

        foreach (var account in accounts)
        {
            if (account.IsBlocked)
            {
                return TraySeverity.Red;
            }

            foreach (var severity in WindowSeverities(account))
            {
                if (Rank(severity) > Rank(worst))
                {
                    worst = severity;
                }
            }
        }

        return worst;
    }

    private static IEnumerable<TraySeverity> WindowSeverities(AccountUsageStatus account)
    {
        if (account.SessionRemainingPercent is { } sessionRemaining)
        {
            yield return Classify(100 - Math.Clamp(sessionRemaining, 0, 100));
        }

        if (account.WeeklyRemainingPercent is { } weeklyRemaining)
        {
            yield return Classify(100 - Math.Clamp(weeklyRemaining, 0, 100));
        }

        foreach (var scoped in account.ScopedQuotas ?? [])
        {
            yield return Classify(Math.Clamp(scoped.UsedPercent, 0, 100));
        }
    }

    private static int Rank(TraySeverity severity) => severity switch
    {
        TraySeverity.Red => 4,
        TraySeverity.Orange => 3,
        TraySeverity.Yellow => 2,
        TraySeverity.Green => 1,
        _ => 0
    };

    /// <summary>Every window's used percentage (0-100) for one account.</summary>
    private static IEnumerable<double> UsedPercents(AccountUsageStatus account)
    {
        return new[] { account.SessionRemainingPercent, account.WeeklyRemainingPercent }
            .Where(value => value.HasValue)
            .Select(value => 100 - Math.Clamp(value!.Value, 0, 100))
            .Concat((account.ScopedQuotas ?? []).Select(q => (double)Math.Clamp(q.UsedPercent, 0, 100)));
    }

    private static List<string> BuildWindowTexts(
        AccountUsageStatus account,
        DateTimeOffset now,
        bool showPercentageLeft)
    {
        var windows = new List<string>(3);
        if (account.IsBlocked)
        {
            // Leads the line: "which window" matters less than "you are stopped".
            windows.Add("blocked");
        }
        if (account.SessionRemainingPercent.HasValue && account.SessionResetsAt.HasValue)
        {
            windows.Add(FormatWindow("Session", 100 - account.SessionRemainingPercent.Value, account.SessionResetsAt.Value, now, weekly: false, showPercentageLeft));
        }
        if (account.WeeklyRemainingPercent.HasValue && account.WeeklyResetsAt.HasValue)
        {
            windows.Add(FormatWindow("Weekly", 100 - account.WeeklyRemainingPercent.Value, account.WeeklyResetsAt.Value, now, weekly: true, showPercentageLeft));
        }
        foreach (var scoped in account.ScopedQuotas ?? [])
        {
            var used = Math.Clamp(scoped.UsedPercent, 0, 100);
            windows.Add(scoped.ResetsAt.HasValue
                ? FormatWindow(scoped.Label, used, scoped.ResetsAt.Value, now, weekly: scoped.Group.Contains("week", StringComparison.OrdinalIgnoreCase), showPercentageLeft)
                : $"{scoped.Label} {UsagePercentageDisplay.Compact(used, showPercentageLeft)}");
        }

        return windows;
    }

    private static string FormatAccount(
        AccountUsageStatus account,
        DateTimeOffset now,
        bool showPercentageLeft)
    {
        var windows = BuildWindowTexts(account, now, showPercentageLeft);
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
        bool weekly,
        bool showPercentageLeft)
    {
        var remaining = resetsAt - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var percent = UsagePercentageDisplay.Value(usedPercent, showPercentageLeft)
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
