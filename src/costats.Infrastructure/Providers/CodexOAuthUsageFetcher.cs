using System.Net.Http.Headers;
using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Fetches Codex (OpenAI) usage data via the ChatGPT backend API.
/// This provides accurate utilization percentages directly from OpenAI.
/// </summary>
public sealed class CodexOAuthUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://chatgpt.com/backend-api/";
    private const string UsagePath = "wham/usage";

    private readonly HttpClient _httpClient;

    public CodexOAuthUsageFetcher()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "costats");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<CodexOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await LoadCredentialsAsync();
            if (credentials?.AccessToken is null)
            {
                return null;
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            // Add account ID header if available
            if (!string.IsNullOrEmpty(credentials.AccountId))
            {
                if (_httpClient.DefaultRequestHeaders.Contains("ChatGPT-Account-Id"))
                {
                    _httpClient.DefaultRequestHeaders.Remove("ChatGPT-Account-Id");
                }
                _httpClient.DefaultRequestHeaders.Add("ChatGPT-Account-Id", credentials.AccountId);
            }

            var response = await _httpClient.GetAsync(UsagePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(content);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<CodexCredentials?> LoadCredentialsAsync()
    {
        // Check for CODEX_HOME environment variable first
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string authPath;

        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            authPath = Path.Combine(codexHome.Trim(), "auth.json");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            authPath = Path.Combine(home, ".codex", "auth.json");
        }

        if (!File.Exists(authPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(authPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try new format with "tokens" object
            if (root.TryGetProperty("tokens", out var tokens))
            {
                return new CodexCredentials(
                    tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                    tokens.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                    tokens.TryGetProperty("account_id", out var aid) ? aid.GetString() : null,
                    tokens.TryGetProperty("id_token", out var idt) ? idt.GetString() : null);
            }

            // Try legacy format with direct OPENAI_API_KEY
            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKey))
            {
                return new CodexCredentials(apiKey.GetString(), null, null, null);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static CodexOAuthUsageResult? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? planType = null;
            double? primaryUsedPercent = null;
            DateTimeOffset? primaryResetsAt = null;
            int? primaryWindowSeconds = null;
            double? secondaryUsedPercent = null;
            DateTimeOffset? secondaryResetsAt = null;
            int? secondaryWindowSeconds = null;
            double? creditBalance = null;
            bool hasCredits = false;

            // Parse plan_type
            if (root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
            {
                planType = pt.GetString();
            }

            // Parse rate_limit
            var limitReached = false;
            if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
            {
                // Primary window (usually 5-hour/session), secondary (usually weekly)
                (primaryUsedPercent, primaryResetsAt, primaryWindowSeconds) = ParseWindow(rateLimit, "primary_window");
                (secondaryUsedPercent, secondaryResetsAt, secondaryWindowSeconds) = ParseWindow(rateLimit, "secondary_window");
                limitReached = IsLimitReached(rateLimit);
            }

            var scopedLimits = ParseAdditionalLimits(root);

            // Parse credits
            if (root.TryGetProperty("credits", out var credits))
            {
                if (credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True)
                {
                    hasCredits = true;
                }
                if (credits.TryGetProperty("balance", out var bal))
                {
                    if (bal.ValueKind == JsonValueKind.Number)
                    {
                        creditBalance = bal.GetDouble();
                    }
                    else if (bal.ValueKind == JsonValueKind.String && double.TryParse(bal.GetString(), out var balVal))
                    {
                        creditBalance = balVal;
                    }
                }
            }

            return new CodexOAuthUsageResult(
                planType,
                primaryUsedPercent,
                primaryResetsAt,
                primaryWindowSeconds,
                secondaryUsedPercent,
                secondaryResetsAt,
                secondaryWindowSeconds,
                hasCredits,
                creditBalance,
                DateTimeOffset.UtcNow,
                limitReached,
                scopedLimits);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads one <c>{used_percent, reset_at, limit_window_seconds}</c> window.</summary>
    private static (double? Used, DateTimeOffset? ResetsAt, int? WindowSeconds) ParseWindow(
        JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        double? used = null;
        DateTimeOffset? resetsAt = null;
        int? windowSeconds = null;

        if (window.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number)
        {
            used = up.GetDouble();
        }
        if (window.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        }
        if (window.TryGetProperty("limit_window_seconds", out var lws) && lws.ValueKind == JsonValueKind.Number)
        {
            windowSeconds = lws.GetInt32();
        }

        return (used, resetsAt, windowSeconds);
    }

    /// <summary>The same fact from both directions: hit the limit, or not allowed through.</summary>
    private static bool IsLimitReached(JsonElement rateLimit)
    {
        if (rateLimit.TryGetProperty("limit_reached", out var reached) && reached.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return rateLimit.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.False;
    }

    /// <summary>
    /// Reads "additional_rate_limits": quotas scoped to a single model or
    /// feature (e.g. GPT-5.3-Codex-Spark), each carrying its own pair of
    /// windows. This is the Codex counterpart of Claude's scoped "limits".
    /// </summary>
    private static IReadOnlyList<CodexScopedLimit> ParseAdditionalLimits(JsonElement root)
    {
        if (!root.TryGetProperty("additional_rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<CodexScopedLimit>();
        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = entry.TryGetProperty("limit_name", out var ln) && ln.ValueKind == JsonValueKind.String
                ? ln.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name) ||
                !entry.TryGetProperty("rate_limit", out var rateLimit) ||
                rateLimit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var primary = ParseWindow(rateLimit, "primary_window");
            var secondary = ParseWindow(rateLimit, "secondary_window");
            if (primary.Used is null && secondary.Used is null)
            {
                continue;
            }

            result.Add(new CodexScopedLimit(
                name!,
                primary.Used,
                primary.ResetsAt,
                primary.WindowSeconds,
                secondary.Used,
                secondary.ResetsAt,
                secondary.WindowSeconds));
        }

        return result;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record CodexCredentials(
        string? AccessToken,
        string? RefreshToken,
        string? AccountId,
        string? IdToken);
}

public sealed record CodexOAuthUsageResult(
    string? PlanType,
    double? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    int? PrimaryWindowSeconds,
    double? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    int? SecondaryWindowSeconds,
    bool HasCredits,
    double? CreditBalance,
    DateTimeOffset FetchedAt,
    bool LimitReached = false,
    IReadOnlyList<CodexScopedLimit>? ScopedLimits = null);

/// <summary>
/// One entry of Codex's <c>additional_rate_limits</c>: a quota that applies to
/// a single model or metered feature rather than to the whole account.
/// </summary>
public sealed record CodexScopedLimit(
    string Name,
    double? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    int? PrimaryWindowSeconds,
    double? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    int? SecondaryWindowSeconds);
