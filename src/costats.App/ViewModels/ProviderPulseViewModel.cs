using CommunityToolkit.Mvvm.ComponentModel;
using costats.Core.Pulse;

namespace costats.App.ViewModels;

public sealed partial class ProviderPulseViewModel : ObservableObject
{
    [ObservableProperty]
    private string providerId = string.Empty;

    /// <summary>Provider family ("claude", "codex", "copilot", "zai") regardless of account suffix.</summary>
    public string ProviderKind
    {
        get
        {
            var separator = ProviderId.IndexOf(':');
            return separator > 0 ? ProviderId[..separator] : ProviderId;
        }
    }

    partial void OnProviderIdChanged(string value) => OnPropertyChanged(nameof(ProviderKind));

    /// <summary>True for the user-selected primary account (pinned to the overview top, drives the tray icon).</summary>
    [ObservableProperty]
    private bool isPrimary;

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string statusSummary = "No data";

    [ObservableProperty]
    private string planText = string.Empty;

    // Session metrics
    [ObservableProperty]
    private bool hasSessionData;

    [ObservableProperty]
    private double sessionProgress;

    [ObservableProperty]
    private string sessionUsageLabel = "--";

    [ObservableProperty]
    private string sessionResetText = string.Empty;

    [ObservableProperty]
    private string sessionPaceText = string.Empty;

    [ObservableProperty]
    private double sessionPaceProgress;

    [ObservableProperty]
    private bool sessionPaceOnTop;

    // Weekly metrics
    [ObservableProperty]
    private bool hasWeekData;

    [ObservableProperty]
    private double weekProgress;

    [ObservableProperty]
    private string weekUsageLabel = "--";

    [ObservableProperty]
    private string weekResetText = string.Empty;

    /// <summary>Model-scoped quota rows (e.g. Claude's Fable weekly limit).</summary>
    [ObservableProperty]
    private IReadOnlyList<ScopedLimitRow> scopedLimits = [];

    [ObservableProperty]
    private bool hasScopedLimits;

    [ObservableProperty]
    private string weekPaceText = string.Empty;

    [ObservableProperty]
    private double weekPaceProgress;

    [ObservableProperty]
    private bool weekPaceOnTop;

    // Extra usage / Credits
    [ObservableProperty]
    private string extraUsageLabel = "--";

    [ObservableProperty]
    private double extraUsageProgress;

    [ObservableProperty]
    private bool hasExtraUsage;

    // Cost tracking
    [ObservableProperty]
    private string todayCostText = "--";

    [ObservableProperty]
    private string monthCostText = "--";

    [ObservableProperty]
    private bool hasCostData;

    // Utilization status for traffic-light indicators (multicc stacked view)
    [ObservableProperty]
    private string sessionStatusColor = "#10B981"; // Green default

    [ObservableProperty]
    private string weekStatusColor = "#10B981";

    [ObservableProperty]
    private string overallStatusText = "OK";

    [ObservableProperty]
    private string overallStatusColor = "#10B981";

    // Readable percentage text for multi-panel hero numbers (WCAG AA contrast on lavender)
    [ObservableProperty]
    private string sessionPercentText = "0%";

    [ObservableProperty]
    private string weekPercentText = "0%";

    [ObservableProperty]
    private string sessionPercentColor = "#047857";

    [ObservableProperty]
    private string weekPercentColor = "#047857";

    // Compact cost line for multicc stacked cards (e.g. "$4.20 today · $82.50 / 30d")
    [ObservableProperty]
    private string compactCostText = string.Empty;

    [ObservableProperty]
    private bool hasCompactCost;

    // Token tracking
    [ObservableProperty]
    private string todayTokensText = "--";

    [ObservableProperty]
    private string monthTokensText = "--";

    // Legacy properties for compatibility
    [ObservableProperty]
    private string sessionText = "--";

    [ObservableProperty]
    private string weekText = "--";

    [ObservableProperty]
    private string creditsText = "--";

    public static ProviderPulseViewModel FromReading(ProviderReading reading, string displayNameFallback)
    {
        var vm = new ProviderPulseViewModel
        {
            ProviderId = reading.Usage?.ProviderId ?? displayNameFallback,
            DisplayName = displayNameFallback,
            StatusSummary = FormatStatusSummary(reading),
            PlanText = reading.Identity?.Plan ?? string.Empty
        };

        PopulateSessionMetrics(vm, reading);
        PopulateWeekMetrics(vm, reading);
        PopulateScopedLimits(vm, reading);
        PopulateExtraUsage(vm, reading);
        PopulateCostData(vm, reading);

        // Set overall status based on the higher of session or week utilization
        var sessionPercent = vm.SessionProgress * 100.0;
        var weekPercent = vm.WeekProgress * 100.0;
        var worstPercent = Math.Max(sessionPercent, weekPercent);
        var worstSeverity = WorstSeverity(reading.Usage);
        vm.OverallStatusColor = GetUtilizationColor(worstPercent, worstSeverity);
        vm.OverallStatusText = GetStatusText(worstPercent, worstSeverity);

        // Being refused is worse than any percentage, and no window on its own
        // says it, so it overrides the headline on every surface that shows one.
        if (reading.Usage?.IsBlocked == true)
        {
            vm.OverallStatusColor = "#EF4444";
            vm.OverallStatusText = "Blocked";
            vm.StatusSummary = "Limit reached - requests are being refused";
        }

        // Legacy fields
        vm.SessionText = FormatUsageRatio(reading.Usage?.SessionUsed, reading.Usage?.SessionLimit);
        vm.WeekText = FormatUsageRatio(reading.Usage?.WeekUsed, reading.Usage?.WeekLimit);
        vm.CreditsText = reading.Usage?.SpendingBucket?.Available.ToString("0.##") ?? "--";

        return vm;
    }

    private static void PopulateSessionMetrics(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var usage = reading.Usage;
        if (usage?.SessionUsed is null || usage.SessionLimit is null)
        {
            return;
        }

        vm.HasSessionData = true;
        var usedPercent = CalculateUsedPercent(usage.SessionUsed, usage.SessionLimit);
        vm.SessionProgress = usedPercent / 100.0;
        vm.SessionUsageLabel = FormatUsageLabel(usedPercent, usage.SessionUsed);

        // Reset text
        if (usage.SessionWindow?.ResetsAt is { } sessionResets)
        {
            vm.SessionResetText = $"Resets {UsageFormatter.ResetCountdown(sessionResets)}";

            // Pace calculation
            var pace = UsagePace.Calculate(
                usedPercent,
                sessionResets,
                usage.SessionWindow.Duration);

            if (pace is not null)
            {
                vm.SessionPaceText = UsageFormatter.FormatPace(pace) ?? string.Empty;
                vm.SessionPaceProgress = pace.ExpectedUsedPercent / 100.0;
                vm.SessionPaceOnTop = pace.DeltaPercent < 0; // Behind = pace marker above actual
            }
        }

        vm.SessionStatusColor = GetUtilizationColor(usedPercent, usage.SessionSeverity);
        vm.SessionPercentText = $"{(int)Math.Round(usedPercent)}%";
        vm.SessionPercentColor = GetPercentTextColor(usedPercent, usage.SessionSeverity);
    }

    private static void PopulateWeekMetrics(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var usage = reading.Usage;
        if (usage?.WeekUsed is null || usage.WeekLimit is null)
        {
            return;
        }

        vm.HasWeekData = true;
        var usedPercent = CalculateUsedPercent(usage.WeekUsed, usage.WeekLimit);
        vm.WeekProgress = usedPercent / 100.0;
        vm.WeekUsageLabel = FormatUsageLabel(usedPercent, usage.WeekUsed);

        // Reset text
        if (usage.WeekWindow?.ResetsAt is { } weekResets)
        {
            vm.WeekResetText = $"Resets {UsageFormatter.ResetCountdown(weekResets)}";

            // Pace calculation
            var pace = UsagePace.Calculate(
                usedPercent,
                weekResets,
                usage.WeekWindow.Duration);

            if (pace is not null)
            {
                vm.WeekPaceText = UsageFormatter.FormatPace(pace) ?? string.Empty;
                vm.WeekPaceProgress = pace.ExpectedUsedPercent / 100.0;
                vm.WeekPaceOnTop = pace.DeltaPercent < 0;
            }
        }

        vm.WeekStatusColor = GetUtilizationColor(usedPercent, usage.WeekSeverity);
        vm.WeekPercentText = $"{(int)Math.Round(usedPercent)}%";
        vm.WeekPercentColor = GetPercentTextColor(usedPercent, usage.WeekSeverity);
    }

    private static void PopulateScopedLimits(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var quotas = reading.Usage?.ScopedQuotas;
        if (quotas is not { Count: > 0 })
        {
            return;
        }

        vm.ScopedLimits = quotas
            .Select(q => new ScopedLimitRow(
                q.Label,
                char.ToUpperInvariant(q.Group[0]) + q.Group[1..].Replace('_', ' '),
                $"{q.UsedPercent}%",
                q.UsedPercent / 100.0,
                GetPercentTextColor(q.UsedPercent, q.Severity),
                q.ResetsAt is { } resets ? $"Resets {UsageFormatter.ResetCountdown(resets)}" : string.Empty))
            .ToList();
        vm.HasScopedLimits = true;
    }

    private static void PopulateExtraUsage(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var bucket = reading.Usage?.SpendingBucket;
        if (bucket is null)
        {
            vm.HasExtraUsage = false;
            vm.ExtraUsageLabel = "--";
            return;
        }

        vm.HasExtraUsage = true;

        switch (bucket.Kind)
        {
            case BucketKind.OverageSpend:
                // Claude-style: show spent / ceiling
                vm.ExtraUsageLabel = $"Overage: {bucket.CurrencySymbol}{bucket.Consumed:F2} / {bucket.CurrencySymbol}{bucket.Ceiling:F2}";
                vm.ExtraUsageProgress = bucket.FillRatio;
                break;

            case BucketKind.PrepaidBalance:
                // Codex-style: show remaining balance
                vm.ExtraUsageLabel = $"Balance: {bucket.CurrencySymbol}{bucket.Available:F2} remaining";
                vm.ExtraUsageProgress = 0; // No progress bar for prepaid
                break;
        }
    }

    private static void PopulateCostData(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var consumption = reading.Usage?.Consumption;
        if (consumption is null || (consumption.TodayTokens.TotalConsumed == 0 && consumption.RollingWindowTokens.TotalConsumed == 0))
        {
            vm.HasCostData = false;
            return;
        }

        vm.HasCostData = true;

        // Today's consumption
        var todayTokens = consumption.TodayTokens.TotalConsumed;
        var todayCost = consumption.TodayCostUsd;
        vm.TodayCostText = UsageFormatter.FormatCurrency(todayCost);
        vm.TodayTokensText = UsageFormatter.FormatTokenCount(todayTokens);

        // Rolling window consumption
        var windowTokens = consumption.RollingWindowTokens.TotalConsumed;
        var windowCost = consumption.RollingWindowCostUsd;
        vm.MonthCostText = UsageFormatter.FormatCurrency(windowCost);
        vm.MonthTokensText = UsageFormatter.FormatTokenCount(windowTokens);

        // Compact single-line cost for stacked multicc cards
        var todayFormatted = UsageFormatter.FormatCurrency(todayCost);
        var monthFormatted = UsageFormatter.FormatCurrency(windowCost);
        vm.CompactCostText = $"{todayFormatted} today  ·  {monthFormatted} / 30d";
        vm.HasCompactCost = true;
    }

    private static double CalculateUsedPercent(long? used, long? limit)
    {
        if (used is null)
        {
            return 0;
        }

        // If limit is 100, the "used" value IS the percentage directly
        // This happens when we get percentage data from CLI probe
        if (limit == 100)
        {
            return Math.Clamp(used.Value, 0, 100);
        }

        if (limit is null || limit <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)used.Value / limit.Value * 100, 0, 100);
    }

    private static string FormatUsageLabel(double usedPercent, long? used)
    {
        if (used is null || used == 0)
        {
            return "0% used";
        }

        return $"{(int)Math.Round(usedPercent)}% used";
    }

    private static string FormatUsageRatio(long? used, long? limit)
    {
        if (used is null && limit is null)
        {
            return "--";
        }

        if (limit is null)
        {
            return used?.ToString() ?? "--";
        }

        return $"{used ?? 0}/{limit.Value}";
    }

    private static string FormatStatusSummary(ProviderReading reading)
    {
        if (reading.StatusSummary is not null)
        {
            return reading.StatusSummary;
        }

        return reading.Source switch
        {
            ReadingSource.LocalLog => $"Updated {UsageFormatter.FormatRelativeTime(reading.CapturedAt)}",
            ReadingSource.Api => "API",
            ReadingSource.Cli => "CLI",
            _ => "No data"
        };
    }

    /// <summary>
    /// A provider that rates its own windows (Claude sends limits[].severity)
    /// decides the colour, so the app agrees with the provider's own UI. The
    /// percentage thresholds are the fallback for providers that report none.
    /// </summary>
    private static string GetUtilizationColor(double percent, QuotaSeverity? severity = null)
    {
        return severity switch
        {
            QuotaSeverity.Critical => "#EF4444",  // Red - at/over limit
            QuotaSeverity.Warning  => "#F59E0B",  // Amber - moderate
            QuotaSeverity.Normal   => "#10B981",  // Green - healthy
            _ => percent switch
            {
                >= 95 => "#EF4444",
                >= 80 => "#F97316",  // Orange - near limit
                >= 50 => "#F59E0B",
                _     => "#10B981",
            }
        };
    }

    private static string GetStatusText(double percent, QuotaSeverity? severity = null)
    {
        return severity switch
        {
            QuotaSeverity.Critical => "At limit",
            QuotaSeverity.Warning  => "Near limit",
            QuotaSeverity.Normal   => "OK",
            _ => percent switch
            {
                >= 95 => "At limit",
                >= 80 => "Near limit",
                >= 50 => "Moderate",
                _     => "OK",
            }
        };
    }

    /// <summary>Worst severity across every window of a reading; null when no window carries one.</summary>
    private static QuotaSeverity? WorstSeverity(UsagePulse? usage)
    {
        if (usage is null)
        {
            return null;
        }

        QuotaSeverity? worst = null;
        foreach (var severity in Severities(usage))
        {
            if (worst is null || severity > worst.Value)
            {
                worst = severity;
            }
        }

        return worst;
    }

    private static IEnumerable<QuotaSeverity> Severities(UsagePulse usage)
    {
        if (usage.SessionSeverity is { } session)
        {
            yield return session;
        }

        if (usage.WeekSeverity is { } week)
        {
            yield return week;
        }

        foreach (var scoped in usage.ScopedQuotas)
        {
            if (scoped.Severity is { } severity)
            {
                yield return severity;
            }
        }
    }

    /// <summary>
    /// Returns WCAG AA-compliant text colors for percentage hero numbers on lavender background.
    /// Darker variants of the bar colors ensure 4.5:1+ contrast ratio.
    /// </summary>
    private static string GetPercentTextColor(double percent, QuotaSeverity? severity = null)
    {
        // On dark surfaces the darkened AA variants lose contrast; use the bright bar colours instead.
        if (costats.App.Services.ThemeManager.IsDark)
        {
            return GetUtilizationColor(percent, severity);
        }

        return severity switch
        {
            QuotaSeverity.Critical => "#DC2626",  // Red-600 (~6.5:1 on lavender)
            QuotaSeverity.Warning  => "#B45309",  // Amber-700 (~5.4:1)
            QuotaSeverity.Normal   => "#047857",  // Emerald-700 (~4.6:1)
            _ => percent switch
            {
                >= 95 => "#DC2626",
                >= 80 => "#C2410C",  // Orange-700 (~6.0:1)
                >= 50 => "#B45309",
                _     => "#047857",
            }
        };
    }
}

/// <summary>One display row for a model-scoped quota window.</summary>
public sealed record ScopedLimitRow(
    string Label,
    string GroupLabel,
    string PercentText,
    double Progress,
    string PercentColor,
    string ResetText);
