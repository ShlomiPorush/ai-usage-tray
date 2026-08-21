using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public sealed record CodexAppServerRateLimitSnapshot(
    double? SessionRemainingPercent,
    TimeSpan? SessionWindowDuration,
    DateTimeOffset? SessionResetsAt,
    double? WeeklyRemainingPercent,
    TimeSpan? WeeklyWindowDuration,
    DateTimeOffset? WeeklyResetsAt,
    string? PlanType = null)
{
    /// <summary>
    /// Per-model windows from <c>rateLimitsByLimitId</c> (e.g. GPT-5.3-Codex-Spark).
    /// The account-wide entry is excluded: it is already the session/weekly pair above.
    /// </summary>
    public IReadOnlyList<ScopedQuota> ScopedQuotas { get; init; } = [];

    /// <summary>
    /// The account itself is being refused right now. Only the account-wide
    /// entry sets this; a spent per-model quota blocks that model, not the account.
    /// </summary>
    public bool IsBlocked { get; init; }
}

public static class CodexAppServerRateLimitParser
{
    /// <summary>Fallback account limit id when the payload omits the top-level one.</summary>
    private const string DefaultAccountLimitId = "codex";

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

            var (session, weekly) = ClassifyWindows(rateLimits);

            string? planType = null;
            if (rateLimits.TryGetProperty("planType", out var plan) && plan.ValueKind == JsonValueKind.String)
            {
                planType = plan.GetString();
            }

            var accountLimitId = ReadString(rateLimits, "limitId") ?? DefaultAccountLimitId;
            var blocked = IsRefused(rateLimits);
            var scoped = ParseScopedQuotas(rateLimits, accountLimitId, ref blocked);

            return new CodexAppServerRateLimitSnapshot(
                session.RemainingPercent,
                session.Duration,
                session.ResetsAt,
                weekly.RemainingPercent,
                weekly.Duration,
                weekly.ResetsAt,
                planType)
            {
                ScopedQuotas = scoped,
                IsBlocked = blocked
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns <c>rateLimitsByLimitId</c> into one scoped row per window. The
    /// account-wide entry is skipped (it duplicates the session/weekly pair) but
    /// still contributes to <paramref name="blocked"/>. Rows at 0% are kept: an
    /// untouched model is a real, useful reading, not missing data.
    /// </summary>
    private static IReadOnlyList<ScopedQuota> ParseScopedQuotas(
        JsonElement rateLimits,
        string accountLimitId,
        ref bool blocked)
    {
        if (!rateLimits.TryGetProperty("rateLimitsByLimitId", out var byLimitId) ||
            byLimitId.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var rows = new List<ScopedQuota>();
        foreach (var property in byLimitId.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entry = property.Value;
            var limitId = ReadString(entry, "limitId") ?? property.Name;
            if (string.Equals(limitId, accountLimitId, StringComparison.OrdinalIgnoreCase))
            {
                blocked |= IsRefused(entry);
                continue;
            }

            var label = ReadString(entry, "limitName") ?? limitId;
            var isActive = HasReachedType(entry);
            var (session, weekly) = ClassifyWindows(entry);

            AddScopedRow(rows, label, "session", session, isActive);
            AddScopedRow(rows, label, "weekly", weekly, isActive);
        }

        return rows;
    }

    private static void AddScopedRow(
        List<ScopedQuota> rows,
        string label,
        string group,
        ParsedWindow window,
        bool isActive)
    {
        if (window.UsedPercent is not { } used)
        {
            return;
        }

        // Codex reports no severity of its own, so the row falls back to the
        // app's percentage thresholds.
        rows.Add(new ScopedQuota(
            label,
            group,
            (long)Math.Round(used),
            window.ResetsAt,
            isActive));
    }

    /// <summary>
    /// The app-server does not guarantee that "primary" means five-hour.
    /// Some plans expose only a seven-day window as primary, and per-model
    /// entries pair a five-hour primary with a seven-day secondary. Classify by
    /// duration and fall back to position only when the duration is unusable.
    /// </summary>
    private static (ParsedWindow Session, ParsedWindow Weekly) ClassifyWindows(JsonElement source)
    {
        var primary = ParseWindow(source, "primary");
        var secondary = ParseWindow(source, "secondary");
        var windows = new[] { primary, secondary }.Where(window => window.HasData).ToArray();

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

        return (session, weekly);
    }

    private static ParsedWindow ParseWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        double? usedPercent = null;
        TimeSpan? duration = null;
        DateTimeOffset? resetsAt = null;

        if (window.TryGetProperty("usedPercent", out var used) && used.ValueKind == JsonValueKind.Number)
        {
            usedPercent = Math.Clamp(used.GetDouble(), 0, 100);
        }

        if (window.TryGetProperty("windowDurationMins", out var minutes) && minutes.ValueKind == JsonValueKind.Number)
        {
            duration = TimeSpan.FromMinutes(minutes.GetDouble());
        }

        if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());
        }

        return new ParsedWindow(usedPercent, duration, resetsAt);
    }

    /// <summary>The same fact from two directions: the limit was hit, or spend control stopped the account.</summary>
    private static bool IsRefused(JsonElement entry) =>
        HasReachedType(entry) ||
        (entry.TryGetProperty("spendControlReached", out var spend) && spend.ValueKind == JsonValueKind.True);

    private static bool HasReachedType(JsonElement entry) =>
        entry.TryGetProperty("rateLimitReachedType", out var reached) &&
        reached.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private readonly record struct ParsedWindow(
        double? UsedPercent,
        TimeSpan? Duration,
        DateTimeOffset? ResetsAt)
    {
        public double? RemainingPercent => UsedPercent.HasValue ? 100 - UsedPercent.Value : null;

        public bool HasData => UsedPercent.HasValue || Duration.HasValue || ResetsAt.HasValue;
    }
}
