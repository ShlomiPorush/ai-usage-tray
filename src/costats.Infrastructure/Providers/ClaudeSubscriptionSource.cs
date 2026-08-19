using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads account-wide Claude subscription quota through a dedicated OAuth profile.
/// The profile may be authenticated by Claude Code, but the returned limits belong
/// to the Claude subscription and include usage from the desktop app.
/// </summary>
public sealed class ClaudeSubscriptionSource : ISignalSource
{
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekDuration = TimeSpan.FromDays(7);
    private readonly IClaudeSubscriptionUsageClient _client;

    public ClaudeSubscriptionSource(IClaudeSubscriptionUsageClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public ProviderProfile Profile => ProviderCatalog.Claude;

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _client.FetchAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (result is null)
        {
            return new ProviderReading(
                null,
                null,
                "Claude subscription is not connected",
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
            CreateWindow(WeekDuration, result.SevenDayResetsAt, weeklyUsed.HasValue));

        return new ProviderReading(
            usage,
            new IdentityCard(
                Profile.ProviderId,
                Profile.DisplayName,
                null,
                null,
                FormatPlan(result.SubscriptionType),
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

    private static string FormatPlan(string? subscriptionType) =>
        string.IsNullOrWhiteSpace(subscriptionType)
            ? string.Empty
            : char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..].ToLowerInvariant();
}
