using System.Text.Json;

namespace costats.Infrastructure.Providers;

internal sealed record ClaudeCredentials(
    string? AccessToken,
    string? RefreshToken,
    long? ExpiresAt,
    string? SubscriptionType,
    string? RateLimitTier);

/// <summary>
/// Reads the OAuth token Claude Code keeps in an account's .credentials.json.
/// The token is used in memory only; it is never logged or copied elsewhere.
/// </summary>
internal static class ClaudeCredentialFile
{
    public static async Task<ClaudeCredentials?> LoadAsync(string? configDir)
    {
        string credentialsPath;
        if (configDir is not null)
        {
            credentialsPath = Path.Combine(configDir, ".credentials.json");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            credentialsPath = Path.Combine(home, ".claude", ".credentials.json");
        }

        if (!File.Exists(credentialsPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(credentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            {
                return null;
            }

            return new ClaudeCredentials(
                oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null,
                oauth.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null,
                oauth.TryGetProperty("expiresAt", out var exp) ? exp.GetInt64() : null,
                oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null,
                oauth.TryGetProperty("rateLimitTier", out var rlt) ? rlt.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsTokenExpired(ClaudeCredentials credentials) =>
        credentials.ExpiresAt.HasValue
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > credentials.ExpiresAt.Value;
}
