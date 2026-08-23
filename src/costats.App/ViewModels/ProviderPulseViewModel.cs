using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Services;
using costats.Core.Analytics;
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

    [ObservableProperty]
    private string email = string.Empty;

    [ObservableProperty]
    private bool isEmailRevealed;

    public bool HasEmail => !string.IsNullOrWhiteSpace(Email);

    public string EmailDisplayText => IsEmailRevealed ? Email : EmailPrivacy.Mask(Email);

    public string EmailToggleLabel => IsEmailRevealed ? "Hide email" : "Show email";

    partial void OnEmailChanged(string value)
    {
        IsEmailRevealed = false;
        OnPropertyChanged(nameof(HasEmail));
        OnPropertyChanged(nameof(EmailDisplayText));
    }

    partial void OnIsEmailRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(EmailDisplayText));
        OnPropertyChanged(nameof(EmailToggleLabel));
    }

    [RelayCommand]
    private void ToggleEmailVisibility() => IsEmailRevealed = !IsEmailRevealed;

    public void HideEmail() => IsEmailRevealed = false;

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

    // Redeemable "usage limit reset" credits. Display only; the redeem action
    // lives in the Codex CLI.
    [ObservableProperty]
    private bool hasResetCredits;

    /// <summary>Detail line, e.g. "1 usage limit reset available, expires Sep 20".</summary>
    [ObservableProperty]
    private string resetCreditsText = string.Empty;

    /// <summary>Overview chip for the same fact: "reset" or "2 resets".</summary>
    [ObservableProperty]
    private string resetCreditsChipText = string.Empty;

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

    // Cost tracking, filled in asynchronously from the local usage analytics
    // engine when the account's detail view is opened.
    [ObservableProperty]
    private string todayCostText = "--";

    [ObservableProperty]
    private string monthCostText = "--";

    [ObservableProperty]
    private bool hasCostData;

    /// <summary>
    /// The analytics bucket this account's cost was read from, or empty while
    /// nothing has been loaded. Drives the "More" link's account filter.
    /// </summary>
    [ObservableProperty]
    private string usageAccountId = string.Empty;

    /// <summary>
    /// Whose numbers the cost rows really are, when they are not only this
    /// account's ("all Codex accounts"). Empty for an exact per-account match.
    /// </summary>
    [ObservableProperty]
    private string costScopeNote = string.Empty;

    // Vivid band colour for the bar fills and the card dot. Identical in both themes.
    [ObservableProperty]
    private string sessionStatusColor = "#10B981"; // Green default

    [ObservableProperty]
    private string weekStatusColor = "#10B981";

    [ObservableProperty]
    private string overallStatusText = "OK";

    [ObservableProperty]
    private string overallStatusColor = "#10B981";

    [ObservableProperty]
    private string sessionPercentText = "0%";

    [ObservableProperty]
    private string weekPercentText = "0%";

    // Hue-matched ink for the percent number: deep in light, light in dark.
    [ObservableProperty]
    private string sessionPercentColor = "#065F46";

    [ObservableProperty]
    private string weekPercentColor = "#065F46";

    // The soft 18 percent band tint behind the percent number. No ring.
    [ObservableProperty]
    private string sessionPercentPillColor = "#2E10B981";

    [ObservableProperty]
    private string weekPercentPillColor = "#2E10B981";

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

    public static ProviderPulseViewModel FromReading(
        ProviderReading reading,
        string displayNameFallback,
        bool showPercentageLeft = false)
    {
        var vm = new ProviderPulseViewModel
        {
            ProviderId = reading.Usage?.ProviderId ?? displayNameFallback,
            DisplayName = displayNameFallback,
            StatusSummary = FormatStatusSummary(reading),
            PlanText = reading.Identity?.Plan ?? string.Empty,
            Email = reading.Identity?.Email?.Trim() ?? string.Empty
        };

        PopulateSessionMetrics(vm, reading, showPercentageLeft);
        PopulateWeekMetrics(vm, reading, showPercentageLeft);
        PopulateScopedLimits(vm, reading, showPercentageLeft);
        PopulateResetCredits(vm, reading);
        PopulateExtraUsage(vm, reading);

        // Set overall status based on the higher of session or week utilization
        var sessionPercent = vm.SessionProgress * 100.0;
        var weekPercent = vm.WeekProgress * 100.0;
        var worstPercent = Math.Max(sessionPercent, weekPercent);
        vm.OverallStatusColor = GetUtilizationColor(worstPercent);
        vm.OverallStatusText = GetStatusText(worstPercent);

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

    private static void PopulateSessionMetrics(
        ProviderPulseViewModel vm,
        ProviderReading reading,
        bool showPercentageLeft)
    {
        var usage = reading.Usage;
        if (usage?.SessionUsed is null || usage.SessionLimit is null)
        {
            return;
        }

        vm.HasSessionData = true;
        var usedPercent = CalculateUsedPercent(usage.SessionUsed, usage.SessionLimit);
        vm.SessionProgress = usedPercent / 100.0;
        vm.SessionUsageLabel = UsagePercentageDisplay.Label(usedPercent, showPercentageLeft);

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

        var band = UsageBands.Of(usedPercent);
        vm.SessionStatusColor = BandPalette.Vivid(band);
        vm.SessionPercentText = UsagePercentageDisplay.Compact(usedPercent, showPercentageLeft);
        vm.SessionPercentColor = BandPalette.Ink(band, ThemeManager.IsDark);
        vm.SessionPercentPillColor = BandPalette.Pill(band);
    }

    private static void PopulateWeekMetrics(
        ProviderPulseViewModel vm,
        ProviderReading reading,
        bool showPercentageLeft)
    {
        var usage = reading.Usage;
        if (usage?.WeekUsed is null || usage.WeekLimit is null)
        {
            return;
        }

        vm.HasWeekData = true;
        var usedPercent = CalculateUsedPercent(usage.WeekUsed, usage.WeekLimit);
        vm.WeekProgress = usedPercent / 100.0;
        vm.WeekUsageLabel = UsagePercentageDisplay.Label(usedPercent, showPercentageLeft);

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

        var band = UsageBands.Of(usedPercent);
        vm.WeekStatusColor = BandPalette.Vivid(band);
        vm.WeekPercentText = UsagePercentageDisplay.Compact(usedPercent, showPercentageLeft);
        vm.WeekPercentColor = BandPalette.Ink(band, ThemeManager.IsDark);
        vm.WeekPercentPillColor = BandPalette.Pill(band);
    }

    private static void PopulateScopedLimits(
        ProviderPulseViewModel vm,
        ProviderReading reading,
        bool showPercentageLeft)
    {
        var quotas = reading.Usage?.ScopedQuotas;
        if (quotas is not { Count: > 0 })
        {
            return;
        }

        // The window group leads the row (as it does for the account-wide
        // Session/Weekly rows) and the model name rides along as a chip, so
        // "Weekly" for one model never reads as "Weekly" for the whole account.
        var isDark = ThemeManager.IsDark;
        vm.ScopedLimits = quotas
            .Select(q =>
            {
                var band = UsageBands.Of(q.UsedPercent);
                return new ScopedLimitRow(
                    q.Label,
                    GroupLabelFor(q.Group),
                    UsagePercentageDisplay.Compact(q.UsedPercent, showPercentageLeft),
                    q.UsedPercent / 100.0,
                    BandPalette.Ink(band, isDark),
                    BandPalette.Pill(band),
                    BandPalette.Vivid(band),
                    q.ResetsAt is { } resets ? $"Resets {UsageFormatter.ResetCountdown(resets)}" : string.Empty);
            })
            .ToList();
        vm.HasScopedLimits = true;
    }

    /// <summary>
    /// Fills the reset-credit line and chip. The provider's count is taken as
    /// given; an expiry is only mentioned when the provider sent one.
    /// </summary>
    private static void PopulateResetCredits(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var available = reading.Usage?.ResetCreditsAvailable ?? 0;
        if (available <= 0)
        {
            return;
        }

        // The expiry is a calendar date to the reader, so it is read in the
        // machine's own time zone, like every other reset time in the widget.
        var expiresOn = reading.Usage?.ResetCreditExpiresAt is { } expiresAt
            ? DateOnly.FromDateTime(expiresAt.ToLocalTime().DateTime)
            : (DateOnly?)null;

        vm.HasResetCredits = true;
        vm.ResetCreditsChipText = UsageFormatter.ResetCreditsChip(available);
        vm.ResetCreditsText = UsageFormatter.ResetCreditsLine(available, expiresOn);
    }

    /// <summary>
    /// Same rule as the remote viewer: a scoped window is either the weekly or
    /// the session bucket, whatever the provider calls its group internally.
    /// </summary>
    private static string GroupLabelFor(string group) =>
        group.Contains("week", StringComparison.OrdinalIgnoreCase) ? "Weekly" : "Session";

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

    /// <summary>
    /// Fills the detail view's Cost section from an analytics report. Tokens
    /// with no known price are never dressed up as free: such a bucket says
    /// "unpriced" where the dollar figure would be.
    /// </summary>
    /// <param name="binding">The analytics bucket the totals came from.</param>
    /// <param name="today">This local day's totals.</param>
    /// <param name="month">The last 30 local days' totals.</param>
    public void ApplyUsageCost(UsageAccountBinding binding, UsageTotals today, UsageTotals month)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(month);

        UsageAccountId = binding.AccountId;
        CostScopeNote = binding.IsMerged ? UsageAccountMap.MergedScopeNote : string.Empty;

        TodayCostText = UsageNumberFormat.CostOrUnpriced(today.CostUsd, today.UnpricedTokens);
        TodayTokensText = UsageNumberFormat.Tokens(today.Tokens.ProcessedTokens);
        MonthCostText = UsageNumberFormat.CostOrUnpriced(month.CostUsd, month.UnpricedTokens);
        MonthTokensText = UsageNumberFormat.Tokens(month.Tokens.ProcessedTokens);

        HasCostData = true;
    }

    /// <summary>
    /// Carries an already-loaded Cost section onto the instance that replaced
    /// this one on a refresh, so the section does not blink out while the next
    /// report is on its way.
    /// </summary>
    public void CopyUsageCostFrom(ProviderPulseViewModel? other)
    {
        if (other is null || !other.HasCostData)
        {
            return;
        }

        UsageAccountId = other.UsageAccountId;
        CostScopeNote = other.CostScopeNote;
        TodayCostText = other.TodayCostText;
        TodayTokensText = other.TodayTokensText;
        MonthCostText = other.MonthCostText;
        MonthTokensText = other.MonthTokensText;
        HasCostData = true;
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
    /// The vivid band colour for a used percentage. The number alone decides;
    /// a provider's own severity rating never moves it.
    /// </summary>
    private static string GetUtilizationColor(double percent) => BandPalette.Vivid(UsageBands.Of(percent));

    private static string GetStatusText(double percent) => UsageBands.StatusText(percent);
}

/// <summary>
/// The locked percent palette. Bands come from <see cref="UsageBands"/>; the
/// vivid colours are identical in both themes and the ink is hue matched to
/// the band, deep in the light theme and light in the dark theme.
/// </summary>
public static class BandPalette
{
    /// <summary>Bar fills, card dots, tray icon, hover-popup dots.</summary>
    public static string Vivid(UsageBand band) => band switch
    {
        UsageBand.Red => "#EF4444",
        UsageBand.Orange => "#F97316",
        UsageBand.Yellow => "#EAB308",
        _ => "#10B981"
    };

    /// <summary>The percent number itself, on top of its tinted pill.</summary>
    public static string Ink(UsageBand band, bool isDark) => isDark
        ? band switch
        {
            UsageBand.Red => "#FCA5A5",
            UsageBand.Orange => "#FDBA74",
            UsageBand.Yellow => "#FDE047",
            _ => "#6EE7B7"
        }
        : band switch
        {
            UsageBand.Red => "#991B1B",
            UsageBand.Orange => "#9A3412",
            UsageBand.Yellow => "#854D0E",
            _ => "#065F46"
        };

    /// <summary>
    /// The pill behind the number: the band colour at 18 percent (0x2E of 255),
    /// no ring. Same value in both themes; it composites over whichever card
    /// brush the active theme supplies.
    /// </summary>
    public static string Pill(UsageBand band) => string.Concat("#2E", Vivid(band).AsSpan(1));
}

/// <summary>One display row for a model-scoped quota window.</summary>
public sealed record ScopedLimitRow(
    string Label,
    string GroupLabel,
    string PercentText,
    double Progress,
    string PercentColor,
    string PillColor,
    string BarColor,
    string ResetText);
