using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public sealed record ClaudeAccountProfile(string Id, string DisplayName, string ConfigDir)
{
    public string ValidatedId
    {
        get
        {
            var normalized = Id.Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, "^[a-z0-9][a-z0-9-]{0,31}$"))
            {
                throw new ArgumentException("Account ID must contain only lowercase letters, numbers, and hyphens.", nameof(Id));
            }

            return normalized;
        }
    }
}

/// <summary>
/// Reads account-wide Claude subscription quota through a local OAuth profile
/// (a CLAUDE_CONFIG_DIR authenticated by Claude Code). The returned limits belong
/// to the Claude subscription and include usage from the desktop app.
/// </summary>
public sealed class ClaudeSubscriptionSource : ISignalSource
{
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekDuration = TimeSpan.FromDays(7);
    private readonly IClaudeSubscriptionUsageClient _client;

    public ClaudeSubscriptionSource(ClaudeAccountProfile account, IClaudeSubscriptionUsageClient client)
    {
        ArgumentNullException.ThrowIfNull(account);
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(account.DisplayName))
        {
            throw new ArgumentException("Account display name is required.", nameof(account));
        }

        Profile = new ProviderProfile($"claude:{account.ValidatedId}", account.DisplayName.Trim(), "#FF7A00");
    }

    public ProviderProfile Profile { get; }

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _client.FetchAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (result is null)
        {
            return new ProviderReading(
                null,
                null,
                $"{Profile.DisplayName}: Claude subscription is not connected",
                now,
                ReadingConfidence.Low,
                ReadingSource.Api);
        }

        var sessionUsed = ToUsedPercent(result.FiveHourUsedPercent);
        var weeklyUsed = ToUsedPercent(result.SevenDayUsedPercent);
        var usage = new UsagePulse(
            Profile.ProviderId,
            result.FetchedAt,
            sessionUsed,
            sessionUsed.HasValue ? 100 : null,
            weeklyUsed,
            weeklyUsed.HasValue ? 100 : null,
            null,
            null,
            CreateWindow(SessionDuration, result.FiveHourResetsAt, sessionUsed.HasValue),
            CreateWindow(WeekDuration, result.SevenDayResetsAt, weeklyUsed.HasValue))
        {
            ScopedQuotas = result.ScopedLimits ?? [],
            SessionSeverity = result.FiveHourSeverity,
            WeekSeverity = result.SevenDaySeverity
        };

        return new ProviderReading(
            usage,
            new IdentityCard(
                Profile.ProviderId,
                Profile.DisplayName,
                null,
                null,
                FormatPlan(result.SubscriptionType, result.RateLimitTier),
                "Claude subscription OAuth"),
            "Updated from Claude subscription",
            result.FetchedAt,
            ReadingConfidence.High,
            ReadingSource.Api);
    }

    private static long? ToUsedPercent(double? value) =>
        value.HasValue ? (long)Math.Round(Math.Clamp(value.Value, 0, 100)) : null;

    private static QuotaWindow? CreateWindow(TimeSpan duration, DateTimeOffset? resetsAt, bool hasUsage) =>
        hasUsage || resetsAt.HasValue ? new QuotaWindow(duration, resetsAt) : null;

    private static string FormatPlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType))
        {
            return string.Empty;
        }

        var plan = char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..].ToLowerInvariant();

        // The tier carries the Max multiplier, e.g. "default_claude_max_5x" / "..._20x".
        var multiplier = System.Text.RegularExpressions.Regex.Match(rateLimitTier ?? string.Empty, "_([0-9]+x)$");
        return multiplier.Success ? plan + " " + multiplier.Groups[1].Value : plan;
    }
}
