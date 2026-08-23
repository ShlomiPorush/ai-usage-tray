using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Identity and plan details returned by the OAuth profile endpoint. The plan
/// fields reflect the account's current subscription, unlike the snapshot in
/// .credentials.json which only updates on token refresh.
/// </summary>
public sealed record ClaudeOAuthProfile(
    string? Email,
    string? SubscriptionType,
    string? RateLimitTier);

public static class ClaudeOAuthProfileParser
{
    public static ClaudeOAuthProfile? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string? email = null;
            if (root.TryGetProperty("account", out var account) &&
                account.ValueKind == JsonValueKind.Object &&
                account.TryGetProperty("email", out var emailProp) &&
                emailProp.ValueKind == JsonValueKind.String)
            {
                var value = emailProp.GetString()?.Trim();
                email = string.IsNullOrWhiteSpace(value) ? null : value;
            }

            string? subscriptionType = null;
            string? rateLimitTier = null;
            if (root.TryGetProperty("organization", out var organization) &&
                organization.ValueKind == JsonValueKind.Object)
            {
                if (organization.TryGetProperty("organization_type", out var orgType) &&
                    orgType.ValueKind == JsonValueKind.String)
                {
                    subscriptionType = NormalizeSubscriptionType(orgType.GetString());
                }

                if (organization.TryGetProperty("rate_limit_tier", out var tier) &&
                    tier.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(tier.GetString()))
                {
                    rateLimitTier = tier.GetString()!.Trim();
                }
            }

            if (email is null && subscriptionType is null && rateLimitTier is null)
            {
                return null;
            }

            return new ClaudeOAuthProfile(email, subscriptionType, rateLimitTier);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The profile reports "claude_max" / "claude_pro" while the credentials
    /// file stores "max" / "pro"; strip the prefix so both feed the same
    /// plan formatting downstream.
    /// </summary>
    private static string? NormalizeSubscriptionType(string? organizationType)
    {
        var value = organizationType?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string Prefix = "claude_";
        return value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && value.Length > Prefix.Length
            ? value[Prefix.Length..]
            : value;
    }
}
