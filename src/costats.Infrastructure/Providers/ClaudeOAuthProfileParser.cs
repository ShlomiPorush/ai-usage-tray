using System.Text.Json;

namespace costats.Infrastructure.Providers;

public static class ClaudeOAuthProfileParser
{
    public static string? ParseEmail(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("account", out var account) ||
                account.ValueKind != JsonValueKind.Object ||
                !account.TryGetProperty("email", out var email) ||
                email.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = email.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
