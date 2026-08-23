using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Z.AI / GLM coding-plan monitor. Reads the 5-hour and weekly windows
/// from Z.AI's quota endpoint using a user-supplied API key. The legacy
/// standard-key setting is accepted as a fallback for existing users.
/// </summary>
public sealed class ZaiUsageSource : ISignalSource
{
    private static readonly TimeSpan FallbackSessionWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan FallbackWeekWindow = TimeSpan.FromDays(7);

    private readonly IZaiUsageClient _client;
    private readonly Func<string?> _codingKeyProvider;
    private readonly Func<string?> _standardKeyProvider;
    private readonly Func<string> _displayNameProvider;

    public ZaiUsageSource(
        IZaiUsageClient client,
        Func<string?> codingKeyProvider,
        Func<string?> standardKeyProvider,
        Func<string> displayNameProvider)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _codingKeyProvider = codingKeyProvider ?? throw new ArgumentNullException(nameof(codingKeyProvider));
        _standardKeyProvider = standardKeyProvider ?? throw new ArgumentNullException(nameof(standardKeyProvider));
        _displayNameProvider = displayNameProvider ?? throw new ArgumentNullException(nameof(displayNameProvider));
    }

    public ProviderProfile Profile => new(
        ProviderId: "zai",
        DisplayName: string.IsNullOrWhiteSpace(_displayNameProvider()) ? "GLM" : _displayNameProvider().Trim(),
        BrandColorHex: "#0F62FE");

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _client
            .FetchAsync(_codingKeyProvider(), _standardKeyProvider(), cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        if (snapshot is null)
        {
            return new ProviderReading(
                Usage: null,
                Identity: new IdentityCard(
                    Profile.ProviderId, Profile.DisplayName, null, null, null, "Z.AI API"),
                StatusSummary: "Z.AI: not configured or API key invalid. Add the key in Settings > Accounts.",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Api);
        }

        var sessionRemaining = snapshot.SessionRemainingPercent;
        var weeklyRemaining = snapshot.WeeklyRemainingPercent;

        // Convert remaining→used so the tray, which shows "used" everywhere,
        // stays consistent. If only "remaining" is reported (single-window
        // shape), surface it as the weekly reading.
        var sessionUsed = sessionRemaining.HasValue
            ? (long)Math.Round(Math.Clamp(100 - sessionRemaining.Value, 0, 100))
            : (long?)null;
        var weeklyUsed = weeklyRemaining.HasValue
            ? (long)Math.Round(Math.Clamp(100 - weeklyRemaining.Value, 0, 100))
            : (long?)null;

        var sessionWindow = snapshot.SessionWindow ?? (sessionRemaining.HasValue ? FallbackSessionWindow : (TimeSpan?)null);
        var weeklyWindow = snapshot.WeeklyWindow ?? (weeklyRemaining.HasValue ? FallbackWeekWindow : (TimeSpan?)null);

        var usage = new UsagePulse(
            ProviderId: Profile.ProviderId,
            CapturedAt: snapshot.FetchedAt,
            SessionUsed: sessionUsed,
            SessionLimit: sessionUsed.HasValue ? 100 : null,
            WeekUsed: weeklyUsed,
            WeekLimit: weeklyUsed.HasValue ? 100 : null,
            SpendingBucket: null,
            Consumption: null,
            SessionWindow: (sessionWindow.HasValue || snapshot.SessionResetsAt.HasValue)
                ? new QuotaWindow(sessionWindow ?? TimeSpan.Zero, snapshot.SessionResetsAt)
                : null,
            WeekWindow: (weeklyWindow.HasValue || snapshot.WeeklyResetsAt.HasValue)
                ? new QuotaWindow(weeklyWindow ?? TimeSpan.Zero, snapshot.WeeklyResetsAt)
                : null);

        return new ProviderReading(
            Usage: usage,
            Identity: new IdentityCard(
                Profile.ProviderId, Profile.DisplayName, null, null, null, "Z.AI API"),
            StatusSummary: string.IsNullOrWhiteSpace(snapshot.PlanName)
                ? "Z.AI coding plan"
                : $"Z.AI · {snapshot.PlanName}",
            CapturedAt: snapshot.FetchedAt,
            Confidence: ReadingConfidence.High,
            Source: ReadingSource.Api);
    }
}
