using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads Z.AI / GLM coding-plan usage from the same quota endpoint used by
/// Z.AI's official <c>glm-plan-usage</c> plugin:
/// <c>https://api.z.ai/api/monitor/usage/quota/limit</c>.
///
/// <para>
/// Both keys are user-supplied in Settings and persisted in Windows Credential
/// Manager. They are sent only to <c>api.z.ai</c> to read the account's quota.
/// </para>
/// </summary>
public sealed class ZaiUsageFetcher : IZaiUsageClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const string QuotaUsagePath = "/api/monitor/usage/quota/limit";

    private readonly HttpClient _httpClient;

    public ZaiUsageFetcher() : this(new HttpClient())
    {
    }

    internal ZaiUsageFetcher(HttpMessageHandler handler) : this(new HttpClient(handler))
    {
    }

    private ZaiUsageFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri("https://api.z.ai/");
        _httpClient.Timeout = RequestTimeout;
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<ZaiUsageSnapshot?> FetchAsync(
        string? codingApiKey,
        string? standardApiKey,
        CancellationToken cancellationToken)
    {
        var coding = await TryFetchAsync(QuotaUsagePath, codingApiKey, cancellationToken).ConfigureAwait(false);
        if (coding is not null)
        {
            return coding;
        }

        // Keep supporting the legacy standard-key setting. The monitor endpoint
        // accepts the account key and reports a coding-plan quota when one is
        // attached to that account.
        if (string.Equals(codingApiKey?.Trim(), standardApiKey?.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return await TryFetchAsync(QuotaUsagePath, standardApiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ZaiUsageSnapshot?> TryFetchAsync(
        string relativePath,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
            // Z.AI's official usage-query plugin sends the API key as the raw
            // Authorization value. TryAddWithoutValidation is required because
            // an API key is not an RFC authentication scheme.
            request.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ZaiResponseParser.Parse(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public interface IZaiUsageClient
{
    Task<ZaiUsageSnapshot?> FetchAsync(
        string? codingApiKey,
        string? standardApiKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// One Z.AI coding-plan or pay-as-you-go reading. All percentages are
/// <i>remaining</i> (0 = quota exhausted, 100 = full quota).
/// </summary>
public sealed record ZaiUsageSnapshot(
    double? SessionRemainingPercent,
    DateTimeOffset? SessionResetsAt,
    TimeSpan? SessionWindow,
    double? WeeklyRemainingPercent,
    DateTimeOffset? WeeklyResetsAt,
    TimeSpan? WeeklyWindow,
    string? PlanName,
    DateTimeOffset FetchedAt);

/// <summary>
/// Parses the JSON returned by Z.AI's quota endpoint. It supports the current
/// <c>data.limits</c> shape and legacy shapes, and returns <c>null</c> for
/// anything else rather than fabricating a value.
/// </summary>
internal static class ZaiResponseParser
{
    public static ZaiUsageSnapshot? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Unwrap common envelopes: { "data": { ... } } or { "code": 200, "data": { ... } }
            JsonElement container;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                container = d;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                container = root;
            }
            else
            {
                return null;
            }

            var plan = TryReadString(container, "plan", "planName", "plan_name", "tier", "level");

            // Current coding-plan responses use data.limits with one CREDIT_LIMIT
            // for the 5-hour window (unit=3) and one for the weekly window
            // (unit=6). The percentage field is used, not remaining.
            var quotaLimits = TryReadQuotaLimits(container);
            if (quotaLimits.Session is not null || quotaLimits.Weekly is not null)
            {
                return new ZaiUsageSnapshot(
                    SessionRemainingPercent: quotaLimits.Session?.RemainingPercent,
                    SessionResetsAt: quotaLimits.Session?.ResetsAt,
                    SessionWindow: quotaLimits.Session?.Window,
                    WeeklyRemainingPercent: quotaLimits.Weekly?.RemainingPercent,
                    WeeklyResetsAt: quotaLimits.Weekly?.ResetsAt,
                    WeeklyWindow: quotaLimits.Weekly?.Window,
                    PlanName: plan,
                    FetchedAt: DateTimeOffset.UtcNow);
            }

            // Window A: 5-hour / session window.
            var session = TryReadWindow(container, "five_hour", "fiveHour", "session", "hourly");
            // Window B: weekly window.
            var weekly = TryReadWindow(container, "weekly", "week", "seven_day", "sevenDay");

            if (session is null && weekly is null)
            {
                // Some endpoints report only a single "remaining" percentage with
                // no window breakdown. Surface that as a generic "coding plan"
                // reading with no reset time, so the user at least sees *something*.
                var flatRemaining = TryReadDouble(container, "remaining", "remaining_percent", "usage", "balance");
                if (flatRemaining.HasValue)
                {
                    return new ZaiUsageSnapshot(
                        SessionRemainingPercent: null,
                        SessionResetsAt: null,
                        SessionWindow: null,
                        WeeklyRemainingPercent: flatRemaining,
                        WeeklyResetsAt: null,
                        WeeklyWindow: null,
                        PlanName: plan,
                        FetchedAt: DateTimeOffset.UtcNow);
                }
                return null;
            }

            return new ZaiUsageSnapshot(
                SessionRemainingPercent: session?.RemainingPercent,
                SessionResetsAt: session?.ResetsAt,
                SessionWindow: session?.Window,
                WeeklyRemainingPercent: weekly?.RemainingPercent,
                WeeklyResetsAt: weekly?.ResetsAt,
                WeeklyWindow: weekly?.Window,
                PlanName: plan,
                FetchedAt: DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ZaiWindow(
        double? RemainingPercent,
        DateTimeOffset? ResetsAt,
        TimeSpan? Window);

    private sealed record ZaiQuotaLimits(ZaiWindow? Session, ZaiWindow? Weekly);

    private static ZaiQuotaLimits TryReadQuotaLimits(JsonElement container)
    {
        if (!container.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return new ZaiQuotaLimits(null, null);
        }

        ZaiWindow? session = null;
        ZaiWindow? weekly = null;

        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var usedPercent = TryReadDouble(limit, "percentage", "used_percent");
            var remainingPercent = usedPercent.HasValue
                ? 100 - Math.Clamp(usedPercent.Value, 0, 100)
                : TryReadRemainingPercent(limit);
            var resetsAt = TryReadDateTime(limit, "nextResetTime", "next_reset_time", "reset_at", "resets_at");
            var unit = TryReadInt32(limit, "unit");
            var number = TryReadDouble(limit, "number") ?? 1;

            if (unit == 3)
            {
                session = new ZaiWindow(remainingPercent, resetsAt, TimeSpan.FromHours(number));
            }
            else if (unit == 6)
            {
                weekly = new ZaiWindow(remainingPercent, resetsAt, TimeSpan.FromDays(7 * number));
            }
            else if (string.Equals(TryReadString(limit, "type"), "TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                session = new ZaiWindow(remainingPercent, resetsAt, TimeSpan.FromHours(5));
            }
        }

        return new ZaiQuotaLimits(session, weekly);
    }

    private static double? TryReadRemainingPercent(JsonElement limit)
    {
        var explicitPercent = TryReadDouble(limit, "remaining_percent", "percent_remaining");
        if (explicitPercent.HasValue)
        {
            return Math.Clamp(explicitPercent.Value, 0, 100);
        }

        var remaining = TryReadDouble(limit, "remaining");
        var total = TryReadDouble(limit, "usage", "total", "limit", "quota");
        if (remaining.HasValue && total.HasValue && total.Value > 0)
        {
            return Math.Clamp(remaining.Value / total.Value * 100, 0, 100);
        }

        var current = TryReadDouble(limit, "currentValue", "current_value", "used");
        if (current.HasValue && total.HasValue && total.Value > 0)
        {
            return 100 - Math.Clamp(current.Value / total.Value * 100, 0, 100);
        }

        return null;
    }

    private static int? TryReadInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static ZaiWindow? TryReadWindow(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // "remaining" / "remaining_percent" / "limit_remaining"
            var remaining = TryReadDouble(window, "remaining", "remaining_percent", "limit_remaining", "percent_remaining");
            // If the response supplies "usage" but not "remaining", derive it.
            if (!remaining.HasValue)
            {
                var used = TryReadDouble(window, "used", "used_percent", "usage", "used_tokens", "used_count");
                var total = TryReadDouble(window, "total", "limit", "quota");
                if (used.HasValue && total.HasValue && total.Value > 0)
                {
                    var pct = 100.0 - Math.Clamp(used.Value / total.Value * 100.0, 0, 100);
                    remaining = pct;
                }
            }

            var resetsAt = TryReadDateTime(window, "reset_at", "resets_at", "reset_time", "next_reset", "reset");
            var duration = TryReadTimeSpanSeconds(window, "window_seconds", "window", "duration_seconds");

            if (remaining.HasValue || resetsAt.HasValue || duration.HasValue)
            {
                return new ZaiWindow(remaining, resetsAt, duration);
            }
        }
        return null;
    }

    private static double? TryReadDouble(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var v))
            {
                continue;
            }
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            {
                return d;
            }
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                return s;
            }
        }
        return null;
    }

    private static string? TryReadString(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        return null;
    }

    private static DateTimeOffset? TryReadDateTime(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var v))
            {
                continue;
            }
            if (v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var unix))
            {
                // Z.AI's quota endpoint currently returns nextResetTime in Unix
                // milliseconds; legacy responses used seconds.
                return Math.Abs(unix) >= 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }
        return null;
    }

    private static TimeSpan? TryReadTimeSpanSeconds(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var v))
            {
                continue;
            }
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            {
                return TimeSpan.FromSeconds(d);
            }
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                return TimeSpan.FromSeconds(s);
            }
        }
        return null;
    }
}
