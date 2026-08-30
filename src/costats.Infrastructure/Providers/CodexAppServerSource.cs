using System.Text.RegularExpressions;
using costats.Application.Pulse;
using costats.Application.SessionActivation;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public sealed record CodexAccountProfile(
    string Id,
    string DisplayName,
    string CodexHome,
    bool KeepSessionActive = false)
{
    public string ValidatedId
    {
        get
        {
            var normalized = Id.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(normalized, "^[a-z0-9][a-z0-9-]{0,31}$"))
            {
                throw new ArgumentException("Account ID must contain only lowercase letters, numbers, and hyphens.", nameof(Id));
            }

            return normalized;
        }
    }
}

public sealed class CodexAppServerSource : ISignalSource
{
    private static readonly TimeSpan SessionRefreshInterval = TimeSpan.FromMinutes(30);
    private readonly CodexAccountProfile _account;
    private readonly ICodexAppServerClient _client;
    private readonly ISessionActivationWindowRegistry? _windowRegistry;
    private DateTimeOffset _nextSessionRefreshAt = DateTimeOffset.MinValue;

    public CodexAppServerSource(
        CodexAccountProfile account,
        ICodexAppServerClient client,
        ISessionActivationWindowRegistry? windowRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(account.DisplayName))
        {
            throw new ArgumentException("Account display name is required.", nameof(account));
        }
        if (string.IsNullOrWhiteSpace(account.CodexHome))
        {
            throw new ArgumentException("CODEX_HOME is required.", nameof(account));
        }

        _account = account;
        _client = client;
        _windowRegistry = windowRegistry;
        Profile = new ProviderProfile($"codex:{account.ValidatedId}", account.DisplayName.Trim(), "#0A84FF");
    }

    public ProviderProfile Profile { get; }

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var refreshToken = _account.KeepSessionActive && now >= _nextSessionRefreshAt;
        if (refreshToken)
        {
            // Mark the cadence before the call so a fast failure cannot turn a
            // one-minute usage interval into a one-minute forced-refresh loop.
            _nextSessionRefreshAt = now + SessionRefreshInterval;
        }

        var snapshot = await _client.FetchAsync(
            _account.CodexHome,
            refreshToken,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new ProviderReading(
                null,
                new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, null, "Codex app-server"),
                "Codex is not installed, not signed in, or did not return rate limits",
                now,
                ReadingConfidence.Low,
                ReadingSource.Api);
        }

        if (snapshot.RequiresSignIn)
        {
            return new ProviderReading(
                null,
                new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, null, "Codex app-server"),
                "Sign-in required - reconnect this Codex account",
                now,
                ReadingConfidence.High,
                ReadingSource.Api,
                ProviderAuthenticationState.SignInRequired);
        }

        var sessionUsed = ToUsedPercent(snapshot.SessionRemainingPercent);
        var weeklyUsed = ToUsedPercent(snapshot.WeeklyRemainingPercent);
        var usage = new UsagePulse(
            Profile.ProviderId,
            now,
            sessionUsed,
            sessionUsed.HasValue ? 100 : null,
            weeklyUsed,
            weeklyUsed.HasValue ? 100 : null,
            CreateWindow(
                snapshot.SessionWindowDuration,
                ResolveSessionReset(snapshot.SessionResetsAt, sessionUsed, now)),
            CreateWindow(
                snapshot.WeeklyWindowDuration,
                weeklyUsed == 0 ? null : snapshot.WeeklyResetsAt))
        {
            ScopedQuotas = snapshot.ScopedQuotas,
            // Only the account-wide entry can block the account; the parser
            // already ignores per-model entries for this flag.
            IsBlocked = snapshot.IsBlocked,
            // Shown, never spent: the app has no redeem path by design.
            ResetCreditsAvailable = snapshot.ResetCreditsAvailable,
            ResetCreditExpiresAt = snapshot.ResetCreditExpiresAt
        };

        return new ProviderReading(
            usage,
            new IdentityCard(Profile.ProviderId, Profile.DisplayName, snapshot.Email, null, FormatPlan(snapshot.PlanType), "Codex app-server"),
            "Updated from official Codex app-server",
            now,
            ReadingConfidence.High,
            ReadingSource.Api,
            ProviderAuthenticationState.Authenticated);
    }

    // ChatGPT plan slugs -> display names (mirrors how the Claude plan chip looks).
    private static string? FormatPlan(string? planType) => planType?.ToLowerInvariant() switch
    {
        null or "" => null,
        "prolite" => "Pro Lite",
        "plus" => "Plus",
        "pro" => "Pro",
        "free" => "Free",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        var other => char.ToUpperInvariant(other[0]) + other[1..]
    };

    private static long? ToUsedPercent(double? remainingPercent) =>
        remainingPercent.HasValue
            ? (long)Math.Round(100 - Math.Clamp(remainingPercent.Value, 0, 100))
            : null;

    private DateTimeOffset? ResolveSessionReset(
        DateTimeOffset? providerReset,
        long? usedPercent,
        DateTimeOffset now)
    {
        if (usedPercent != 0)
        {
            return providerReset;
        }

        return _windowRegistry?.TryGetActive(Profile.ProviderId, now, out var confirmedReset) == true
            ? confirmedReset
            : null;
    }

    private static QuotaWindow? CreateWindow(
        TimeSpan? duration,
        DateTimeOffset? resetsAt) =>
        duration.HasValue || resetsAt.HasValue
            ? new QuotaWindow(duration ?? TimeSpan.Zero, resetsAt)
            : null;
}
