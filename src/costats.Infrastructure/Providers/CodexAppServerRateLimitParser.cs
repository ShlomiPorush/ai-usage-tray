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

    /// <summary>
    /// Redeemable "usage limit reset" credits, from the result-level
    /// <c>rateLimitResetCredits.availableCount</c>. The count is authoritative:
    /// the accompanying credit list may be null or shorter than the count.
    /// </summary>
    public long ResetCreditsAvailable { get; init; }

    /// <summary>
    /// When the first redeemable reset credit expires, when the payload lists
    /// it. Null when the list is absent, truncated, or carries no expiry.
    /// </summary>
    public DateTimeOffset? ResetCreditExpiresAt { get; init; }

    /// <summary>Account email returned by <c>account/read</c>, when available.</summary>
    public string? Email { get; init; }

    /// <summary>
    /// The official app-server reported no usable OpenAI account, or a forced
    /// managed-token refresh failed. No credentials are inspected by this app.
    /// </summary>
    public bool RequiresSignIn { get; init; }
}

public sealed record CodexAppServerAccountSnapshot(
    string? Email,
    bool HasAccount,
    bool RequiresOpenaiAuth,
    bool HasError);

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
            // A sibling of rateLimits, not a member of the snapshot: planType and
            // credits live inside rateLimits, this one does not.
            var resetCredits = ParseResetCredits(result);

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
                IsBlocked = blocked,
                ResetCreditsAvailable = resetCredits.Available,
                ResetCreditExpiresAt = resetCredits.ExpiresAt
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the response to <c>account/read</c>. A matching error or a
    /// non-ChatGPT account still counts as a completed response with no email.
    /// </summary>
    public static bool TryParseAccount(
        string json,
        long expectedId,
        out CodexAppServerAccountSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                id.GetInt64() != expectedId)
            {
                return false;
            }

            if (root.TryGetProperty("error", out _))
            {
                snapshot = new CodexAppServerAccountSnapshot(null, false, false, true);
                return true;
            }

            string? email = null;
            var hasAccount = false;
            if (root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("account", out var account) &&
                    account.ValueKind == JsonValueKind.Object)
                {
                    hasAccount = true;
                    email = ReadString(account, "email")?.Trim();
                }

                var requiresOpenaiAuth =
                    result.TryGetProperty("requiresOpenaiAuth", out var requiresAuth) &&
                    requiresAuth.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    requiresAuth.GetBoolean();
                snapshot = new CodexAppServerAccountSnapshot(
                    email,
                    hasAccount,
                    requiresOpenaiAuth,
                    HasError: false);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseAccountEmail(string json, long expectedId, out string? email)
    {
        var matched = TryParseAccount(json, expectedId, out var snapshot);
        email = snapshot?.Email;
        return matched;
    }

    public static bool TryParseError(string json, long expectedId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                id.GetInt64() != expectedId ||
                !root.TryGetProperty("error", out var errorElement))
            {
                return false;
            }

            error = errorElement.ValueKind == JsonValueKind.Object &&
                    errorElement.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : errorElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the result-level <c>rateLimitResetCredits</c> block. The field can
    /// vanish between calls (like <c>rateLimitsByLimitId</c>), so its absence
    /// simply means "no resets to show". <c>availableCount</c> is the only
    /// number trusted for the count: the credit list may be null or truncated,
    /// and is used purely to pick up an expiry date.
    /// </summary>
    private static (long Available, DateTimeOffset? ExpiresAt) ParseResetCredits(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var block) ||
            block.ValueKind != JsonValueKind.Object)
        {
            return (0, null);
        }

        long available = 0;
        if (block.TryGetProperty("availableCount", out var count) &&
            count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt64(out var parsedCount))
        {
            available = Math.Max(0, parsedCount);
        }

        if (available == 0 ||
            !block.TryGetProperty("credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
        {
            return (available, null);
        }

        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(credit, "status"), "available", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (
                available,
                credit.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeSeconds(expires.GetInt64())
                    : null);
        }

        return (available, null);
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
