using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads Z.AI / GLM coding-plan usage from the official
/// <c>https://api.z.ai/api/coding/paas/v4/usage</c> endpoint, authenticated
/// with a user-supplied Bearer token. The endpoint is documented in the
/// Z.AI API reference and returns 401 without an <c>Authorization</c>
/// header. The pay-as-you-go variant is read from
/// <c>/api/paas/v4/usage</c> when the coding plan returns no usage.
///
/// <para>
/// Both keys are user-supplied through <c>appsettings.json</c> under
/// <c>ZAiApiKey</c> and <c>ZAiCodingApiKey</c>. The keys never leave the
/// local machine: they are used only to make outbound HTTPS calls to
/// <c>api.z.ai</c>.
/// </para>
/// </summary>
public sealed class ZaiUsageFetcher : IZaiUsageClient, IZaiModelUsageClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const string CodingUsagePath = "/api/coding/paas/v4/usage";
    private const string StandardUsagePath = "/api/paas/v4/usage";
    private const string ModelUsagePath = "/api/monitor/usage/model-usage";

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
        var coding = await TryFetchAsync(CodingUsagePath, codingApiKey, cancellationToken).ConfigureAwait(false);
        if (coding is not null)
        {
            return coding;
        }

        // No coding-plan subscription, or the key wasn't accepted. Fall back
        // to the standard paas endpoint (pay-as-you-go balance), if a key is
        // available.
        return await TryFetchAsync(StandardUsagePath, standardApiKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ZaiModelUsageSnapshot?> FetchModelUsageAsync(
        string? codingApiKey,
        string? standardApiKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var coding = await TryFetchModelUsageAsync(codingApiKey, from, to, cancellationToken).ConfigureAwait(false);
        if (coding is not null)
        {
            return coding;
        }

        if (string.Equals(codingApiKey?.Trim(), standardApiKey?.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return await TryFetchModelUsageAsync(standardApiKey, from, to, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ZaiModelUsageSnapshot?> TryFetchModelUsageAsync(
        string? apiKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var start = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00";
        var end = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 23:59:59";
        var relativePath = $"{ModelUsagePath}?startTime={Uri.EscapeDataString(start)}&endTime={Uri.EscapeDataString(end)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
            request.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ZaiModelUsageResponseParser.Parse(body);
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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
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

public interface IZaiModelUsageClient
{
    Task<ZaiModelUsageSnapshot?> FetchModelUsageAsync(
        string? codingApiKey,
        string? standardApiKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken);
}

public sealed record ZaiModelUsageSeries(
    string ModelName,
    IReadOnlyList<long> TokensByDay,
    long TotalTokens);

public sealed record ZaiModelUsageSnapshot(
    IReadOnlyList<DateOnly> Days,
    IReadOnlyList<long> TokensByDay,
    IReadOnlyList<long> CallsByDay,
    IReadOnlyList<ZaiModelUsageSeries> Models)
{
    public long TotalTokens => TokensByDay.Sum();
    public long TotalCalls => CallsByDay.Sum();
}

internal static class ZaiModelUsageResponseParser
{
    public static ZaiModelUsageSnapshot? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var data = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : root;

            if (data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("x_time", out var datesElement) ||
                datesElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var days = datesElement.EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String &&
                    DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day)
                        ? day
                        : (DateOnly?)null)
                .Where(day => day.HasValue)
                .Select(day => day!.Value)
                .ToList();
            if (days.Count == 0)
            {
                return null;
            }

            var tokens = ReadLongArray(data, "tokensUsage", days.Count);
            var calls = ReadLongArray(data, "modelCallCount", days.Count);
            var models = new List<ZaiModelUsageSeries>();
            if (data.TryGetProperty("modelDataList", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in modelsElement.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object ||
                        !model.TryGetProperty("modelName", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(nameElement.GetString()))
                    {
                        continue;
                    }

                    var modelTokens = ReadLongArray(model, "tokensUsage", days.Count);
                    var total = TryReadLong(model, "totalTokens") ?? modelTokens.Sum();
                    models.Add(new ZaiModelUsageSeries(nameElement.GetString()!.Trim(), modelTokens, total));
                }
            }

            return new ZaiModelUsageSnapshot(days, tokens, calls, models);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<long> ReadLongArray(JsonElement parent, string name, int count)
    {
        var result = new long[count];
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var index = 0;
        foreach (var value in array.EnumerateArray())
        {
            if (index >= count)
            {
                break;
            }

            result[index++] = ReadLong(value);
        }

        return result;
    }

    private static long? TryReadLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? ReadLong(value) : null;

    private static long ReadLong(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer))
            {
                return Math.Max(0, integer);
            }

            if (value.TryGetDouble(out var number) && double.IsFinite(number))
            {
                return Math.Max(0, (long)Math.Round(number));
            }
        }

        return 0;
    }
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
/// Parses the JSON returned by Z.AI's usage endpoints. The exact shape is
/// not yet published as a stable schema; this parser accepts the two most
/// plausible shapes and returns <c>null</c> for anything else so the tray
/// app shows "Z.AI: no data" rather than a fabricated value.
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

            var plan = TryReadString(container, "plan", "planName", "plan_name", "tier");

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
                return DateTimeOffset.FromUnixTimeSeconds(unix);
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
