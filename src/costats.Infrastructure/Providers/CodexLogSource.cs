using costats.Application.Pulse;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Usage;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public sealed class CodexLogSource : ISignalSource
{
    private static readonly TimeSpan DefaultSessionDuration = TimeSpan.FromHours(3);
    private static readonly TimeSpan DefaultWeekDuration = TimeSpan.FromDays(7);

    private readonly UsageLogScanner _scanner = new();
    private readonly CodexOAuthUsageFetcher _oauthFetcher = new();
    private readonly ExpenseAnalyzer _expenseAnalyzer = new();

    public ProviderProfile Profile => ProviderCatalog.Codex;

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // OAuth is a network call - run in parallel with file I/O
        var oauthTask = _oauthFetcher.FetchAsync(cancellationToken);

        // Log scan and expense analysis both read the same files - run sequentially to halve peak memory
        var logResult = await _scanner.ScanCodexAsync(cancellationToken).ConfigureAwait(false);
        var consumption = await SafeAnalyzeExpenseAsync(cancellationToken).ConfigureAwait(false);

        var oauthResult = await oauthTask.ConfigureAwait(false);

        if (oauthResult is null && logResult.SessionTokens == 0 && logResult.WeekTokens == 0)
        {
            return new ProviderReading(
                Usage: null,
                Identity: null,
                StatusSummary: "No Codex usage data available",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.LocalLog);
        }

        // Prefer OAuth data for percentages, classified by window duration:
        // "primary_window" is not always the five-hour one.
        var (sessionWindowData, weeklyWindowData) = ClassifyWindows(oauthResult);
        var sessionUsedPercent = sessionWindowData.UsedPercent;
        var weeklyUsedPercent = weeklyWindowData.UsedPercent;

        // Get window durations from API or use defaults
        var sessionDuration = sessionWindowData.WindowSeconds is { } sessionSeconds
            ? TimeSpan.FromSeconds(sessionSeconds)
            : DefaultSessionDuration;

        var weekDuration = weeklyWindowData.WindowSeconds is { } weeklySeconds
            ? TimeSpan.FromSeconds(weeklySeconds)
            : DefaultWeekDuration;

        var sessionResetsAt = sessionWindowData.ResetsAt ?? CalculateSessionReset(logResult.SessionStart, now, sessionDuration);
        var weeklyResetsAt = weeklyWindowData.ResetsAt ?? CalculateWeeklyReset(now);

        var sessionWindow = new QuotaWindow(sessionDuration, sessionResetsAt);
        var weekWindow = new QuotaWindow(weekDuration, weeklyResetsAt);

        // Use percentage data directly when available
        long? sessionUsed;
        long? sessionLimit;
        long? weekUsed;
        long? weekLimit;

        if (sessionUsedPercent is not null)
        {
            // Store percentage directly: used=percentage, limit=100
            sessionUsed = (long)Math.Round(sessionUsedPercent.Value);
            sessionLimit = 100;
        }
        else
        {
            sessionUsed = logResult.SessionTokens > 0 ? logResult.SessionTokens : null;
            sessionLimit = null;
        }

        if (weeklyUsedPercent is not null)
        {
            weekUsed = (long)Math.Round(weeklyUsedPercent.Value);
            weekLimit = 100;
        }
        else
        {
            weekUsed = logResult.WeekTokens > 0 ? logResult.WeekTokens : null;
            weekLimit = null;
        }

        // Build prepaid balance bucket when credits are available
        MonetaryBucket? spendingBucket = null;
        if (oauthResult is { HasCredits: true, CreditBalance: not null } && oauthResult.CreditBalance.Value > 0)
        {
            spendingBucket = MonetaryBucket.ForPrepaidBalance((decimal)oauthResult.CreditBalance.Value);
        }

        var usage = new UsagePulse(
            ProviderId: Profile.ProviderId,
            CapturedAt: oauthResult?.FetchedAt ?? logResult.LatestTimestamp ?? now,
            SessionUsed: sessionUsed,
            SessionLimit: sessionLimit,
            WeekUsed: weekUsed,
            WeekLimit: weekLimit,
            SpendingBucket: spendingBucket,
            Consumption: consumption,
            SessionWindow: sessionWindow,
            WeekWindow: weekWindow)
        {
            ScopedQuotas = BuildScopedQuotas(oauthResult),
            IsBlocked = oauthResult?.LimitReached ?? false
        };

        var planText = FormatPlanText(oauthResult?.PlanType);
        var statusSummary = oauthResult is not null
            ? $"Updated {FormatRelativeTime(oauthResult.FetchedAt, now)}"
            : $"Updated {FormatRelativeTime(logResult.LatestTimestamp ?? now, now)}";

        var confidence = oauthResult is not null ? ReadingConfidence.High : ReadingConfidence.Medium;
        var source = oauthResult is not null ? ReadingSource.Api : ReadingSource.LocalLog;

        return new ProviderReading(
            Usage: usage,
            Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, planText, "OAuth"),
            StatusSummary: statusSummary,
            CapturedAt: usage.CapturedAt,
            Confidence: confidence,
            Source: source);
    }

    private static string FormatPlanText(string? planType)
    {
        if (string.IsNullOrEmpty(planType))
        {
            return "Pro";
        }

        // Convert "pro" to "Pro", "plus" to "Plus", etc.
        return char.ToUpper(planType[0]) + planType[1..].ToLower();
    }

    /// <summary>
    /// Turns Codex's per-model limits into scoped rows, one per window. A model
    /// sitting at 0% is kept: an untouched quota is a real reading, and hiding
    /// it would make rows appear and disappear as models are used.
    /// </summary>
    private static IReadOnlyList<ScopedQuota> BuildScopedQuotas(CodexOAuthUsageResult? oauth)
    {
        if (oauth?.ScopedLimits is not { Count: > 0 } limits)
        {
            return [];
        }

        var rows = new List<ScopedQuota>();
        foreach (var limit in limits)
        {
            AddScopedRow(rows, limit.Name, limit.PrimaryUsedPercent, limit.PrimaryResetsAt, limit.PrimaryWindowSeconds);
            AddScopedRow(rows, limit.Name, limit.SecondaryUsedPercent, limit.SecondaryResetsAt, limit.SecondaryWindowSeconds);
        }

        return rows;
    }

    private static void AddScopedRow(
        List<ScopedQuota> rows,
        string name,
        double? usedPercent,
        DateTimeOffset? resetsAt,
        int? windowSeconds)
    {
        if (usedPercent is null)
        {
            return;
        }

        rows.Add(new ScopedQuota(
            name,
            // A day or more is the weekly bucket; anything shorter is the session one.
            windowSeconds is null or >= 86400 ? "weekly" : "session",
            (long)Math.Round(Math.Clamp(usedPercent.Value, 0, 100)),
            resetsAt,
            IsActive: false));
    }

    /// <summary>One OAuth-reported quota window, before it is known to be the session or the weekly one.</summary>
    private readonly record struct OAuthWindow(double? UsedPercent, int? WindowSeconds, DateTimeOffset? ResetsAt)
    {
        public bool HasData => UsedPercent.HasValue || WindowSeconds.HasValue || ResetsAt.HasValue;
    }

    /// <summary>
    /// Codex does not guarantee that "primary_window" is the five-hour one:
    /// some plans report a seven-day window there. Classify by duration and
    /// fall back to position only when the duration is missing.
    /// </summary>
    private static (OAuthWindow Session, OAuthWindow Weekly) ClassifyWindows(CodexOAuthUsageResult? oauth)
    {
        var primary = new OAuthWindow(oauth?.PrimaryUsedPercent, oauth?.PrimaryWindowSeconds, oauth?.PrimaryResetsAt);
        var secondary = new OAuthWindow(oauth?.SecondaryUsedPercent, oauth?.SecondaryWindowSeconds, oauth?.SecondaryResetsAt);
        var windows = new[] { primary, secondary }.Where(window => window.HasData).ToArray();

        // Six hours or less is a session window; a day or more is the weekly one.
        var session = windows.FirstOrDefault(window => window.WindowSeconds is > 0 and <= 21600);
        var weekly = windows.FirstOrDefault(window => window.WindowSeconds is >= 86400);

        if (!session.HasData && primary.HasData && primary != weekly)
        {
            session = primary;
        }
        if (!weekly.HasData && secondary.HasData && secondary != session)
        {
            weekly = secondary;
        }

        return (session, weekly);
    }

    private static DateTimeOffset? CalculateSessionReset(DateTimeOffset? sessionStart, DateTimeOffset now, TimeSpan sessionDuration)
    {
        if (sessionStart is null)
        {
            return now + sessionDuration;
        }

        var elapsed = now - sessionStart.Value;
        if (elapsed >= sessionDuration)
        {
            return now + sessionDuration;
        }

        return sessionStart.Value + sessionDuration;
    }

    private static DateTimeOffset CalculateWeeklyReset(DateTimeOffset now)
    {
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0 && now.TimeOfDay > TimeSpan.Zero)
        {
            daysUntilMonday = 7;
        }

        var nextMonday = now.Date.AddDays(daysUntilMonday);
        return new DateTimeOffset(nextMonday, TimeSpan.Zero);
    }

    private async Task<ConsumptionDigest?> SafeAnalyzeExpenseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _expenseAnalyzer.AnalyzeCodexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cost analysis failure should not break usage display
            return null;
        }
    }
}
