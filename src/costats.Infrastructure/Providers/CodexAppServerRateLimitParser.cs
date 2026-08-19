using System.Text.Json;

namespace costats.Infrastructure.Providers;

public sealed record CodexAppServerRateLimitSnapshot(
    double? SessionRemainingPercent,
    TimeSpan? SessionWindowDuration,
    DateTimeOffset? SessionResetsAt,
    double? WeeklyRemainingPercent,
    TimeSpan? WeeklyWindowDuration,
    DateTimeOffset? WeeklyResetsAt);

public static class CodexAppServerRateLimitParser
{
    public static CodexAppServerRateLimitSnapshot? Parse(string json, long expectedId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                id.GetInt64() != expectedId ||
                root.TryGetProperty("error", out _))
            {
                return null;
            }

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("rateLimits", out var rateLimits))
            {
                return null;
            }

            var primary = ParseWindow(rateLimits, "primary");
            var secondary = ParseWindow(rateLimits, "secondary");
            var windows = new[] { primary, secondary }.Where(window => window.HasData).ToArray();

            // The app-server does not guarantee that "primary" means five-hour.
            // Some plans expose only a seven-day window as primary. Classify by duration.
            var session = windows.FirstOrDefault(window =>
                window.Duration.HasValue && window.Duration.Value <= TimeSpan.FromHours(6));
            var weekly = windows.FirstOrDefault(window =>
                window.Duration.HasValue && window.Duration.Value >= TimeSpan.FromDays(1));

            if (!session.HasData && primary.HasData && primary != weekly)
            {
                session = primary;
            }
            if (!weekly.HasData && secondary.HasData && secondary != session)
            {
                weekly = secondary;
            }

            return new CodexAppServerRateLimitSnapshot(
                session.RemainingPercent,
                session.Duration,
                session.ResetsAt,
                weekly.RemainingPercent,
                weekly.Duration,
                weekly.ResetsAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ParsedWindow ParseWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        double? remaining = null;
        TimeSpan? duration = null;
        DateTimeOffset? resetsAt = null;

        if (window.TryGetProperty("usedPercent", out var used) && used.ValueKind == JsonValueKind.Number)
        {
            remaining = 100 - Math.Clamp(used.GetDouble(), 0, 100);
        }

        if (window.TryGetProperty("windowDurationMins", out var minutes) && minutes.ValueKind == JsonValueKind.Number)
        {
            duration = TimeSpan.FromMinutes(minutes.GetDouble());
        }

        if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());
        }

        return new ParsedWindow(remaining, duration, resetsAt);
    }

    private readonly record struct ParsedWindow(
        double? RemainingPercent,
        TimeSpan? Duration,
        DateTimeOffset? ResetsAt)
    {
        public bool HasData => RemainingPercent.HasValue || Duration.HasValue || ResetsAt.HasValue;
    }
}
