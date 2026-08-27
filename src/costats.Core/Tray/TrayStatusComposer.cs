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

    public double? GetDisplayPercent(bool showRemainingPercentages) =>
        HighestUsedPercent is { } used
            ? UsageDisplay.Percent(used, showRemainingPercentages)
            : null;
}

public static class TrayStatusComposer
{
    public const int MaximumTooltipLength = 127;

    public static TrayStatus Compose(
        IEnumerable<AccountUsageStatus> accounts,
        DateTimeOffset now,
        bool showRemainingPercentages = false,
        bool showWeeklyBeforeSession = false)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var materialized = accounts.ToArray();
        var usedValues = materialized
            .SelectMany(UsedPercents)
            .ToArray();

        var highest = usedValues.Length == 0 ? (double?)null : usedValues.Max();
        var severity = ComposeSeverity(materialized);

        var fullTooltip = string.Join('\n', materialized.Select(account =>
            FormatAccount(account, now, showRemainingPercentages, showWeeklyBeforeSession)));
        var tooltip = fullTooltip.Length > MaximumTooltipLength
            ? fullTooltip[..MaximumTooltipLength]
            : fullTooltip;

        return new TrayStatus(highest, severity, tooltip) { FullTooltip = fullTooltip };
    }

    /// <summary>
    /// One display row per account for rich (non-shell) tooltips: label, the
    /// formatted windows text, and the worst (highest) used percentage for colouring.
    /// </summary>
    public static IReadOnlyList<TrayAccountRow> ComposeRows(
        IEnumerable<AccountUsageStatus> accounts,
        DateTimeOffset now,
        bool showRemainingPercentages = false,
        bool showWeeklyBeforeSession = false)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.Select(account =>
        {
            var windows = BuildWindowTexts(
                account,
                now,
                showRemainingPercentages,
                showWeeklyBeforeSession);
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
        bool showRemainingPercentages,
        bool showWeeklyBeforeSession)
    {
        var windows = new List<string>(3);
        if (account.IsBlocked)
        {
            // Leads the line: "which window" matters less than "you are stopped".
            windows.Add("blocked");
        }
        if (showWeeklyBeforeSession)
        {
            AddWeeklyWindow(windows, account, now, showRemainingPercentages);
            AddSessionWindow(windows, account, now, showRemainingPercentages);
        }
        else
        {
            AddSessionWindow(windows, account, now, showRemainingPercentages);
            AddWeeklyWindow(windows, account, now, showRemainingPercentages);
        }
        foreach (var scoped in account.ScopedQuotas ?? [])
        {
            var used = Math.Clamp(scoped.UsedPercent, 0, 100);
            var displayPercent = showRemainingPercentages ? 100 - used : used;
            windows.Add(scoped.ResetsAt.HasValue
                ? FormatWindow(scoped.Label, displayPercent, scoped.ResetsAt.Value, now, weekly: scoped.Group.Contains("week", StringComparison.OrdinalIgnoreCase))
                : $"{scoped.Label} {displayPercent}%");
        }

        return windows;
    }

    private static void AddSessionWindow(
        ICollection<string> windows,
        AccountUsageStatus account,
        DateTimeOffset now,
        bool showRemainingPercentages)
    {
        if (account.SessionRemainingPercent is not { } sessionRemaining)
        {
            return;
        }

        var display = showRemainingPercentages ? sessionRemaining : 100 - sessionRemaining;
        windows.Add(account.SessionResetsAt.HasValue
            ? FormatWindow("Session", display, account.SessionResetsAt.Value, now, weekly: false)
            : FormatPercentOnly("Session", display));
    }

    private static void AddWeeklyWindow(
        ICollection<string> windows,
        AccountUsageStatus account,
        DateTimeOffset now,
        bool showRemainingPercentages)
    {
        if (account.WeeklyRemainingPercent is not { } weeklyRemaining)
        {
            return;
        }

        var display = showRemainingPercentages ? weeklyRemaining : 100 - weeklyRemaining;
        windows.Add(account.WeeklyResetsAt.HasValue
            ? FormatWindow("Weekly", display, account.WeeklyResetsAt.Value, now, weekly: true)
            : FormatPercentOnly("Weekly", display));
    }

    private static string FormatPercentOnly(string label, double displayPercent)
    {
        var percent = Math.Clamp(displayPercent, 0, 100)
            .ToString("0", CultureInfo.InvariantCulture);
        return $"{label} {percent}%";
    }

    private static string FormatAccount(
        AccountUsageStatus account,
        DateTimeOffset now,
        bool showRemainingPercentages,
        bool showWeeklyBeforeSession)
    {
        var windows = BuildWindowTexts(
            account,
            now,
            showRemainingPercentages,
            showWeeklyBeforeSession);
        return windows.Count == 0
            ? $"{account.Label} unavailable"
            : $"{account.Label} {string.Join(" | ", windows)}";
    }

    /// <summary>
    /// Compact window text, e.g. "Session 86% · 1h22m". The caller supplies
    /// either used or remaining percentage according to the display setting.
    /// </summary>
    private static string FormatWindow(
        string label,
        double displayPercent,
        DateTimeOffset resetsAt,
        DateTimeOffset now,
        bool weekly)
    {
        var remaining = resetsAt - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var percent = Math.Clamp(displayPercent, 0, 100)
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
