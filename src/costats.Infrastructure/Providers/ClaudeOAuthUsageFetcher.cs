using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public interface IClaudeSubscriptionUsageClient
{
    Task<ClaudeOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fetches Claude usage data via the Anthropic OAuth API.
/// </summary>
public sealed class ClaudeOAuthUsageFetcher : IClaudeSubscriptionUsageClient, IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string UsagePath = "/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    private static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromHours(6);

    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan RefreshCooldownSuccess = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshCooldownFailure = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;
    private readonly string? _configDir;

    private int _consecutiveFailures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private string? _lastCredentialFingerprint;
    private ClaudeOAuthUsageResult? _memoryCache;
    private DateTimeOffset _memoryCacheWrittenAt = DateTimeOffset.MinValue;
    private DateTimeOffset _refreshBlockedUntil = DateTimeOffset.MinValue;

    public ClaudeOAuthUsageFetcher()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("anthropic-beta", BetaHeader);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "claude-code/2.1.70");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public ClaudeOAuthUsageFetcher(string configDir) : this()
    {
        _configDir = configDir;
    }

    //  Public API
    public async Task<ClaudeOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken)
    {
        // 1. Load credentials & detect changes
        var credentials = await LoadCredentialsAsync(_configDir);
        var fingerprint = ComputeFingerprint(credentials);

        if (fingerprint != _lastCredentialFingerprint)
        {
            _lastCredentialFingerprint = fingerprint;
            _consecutiveFailures = 0;
            _blockedUntil = DateTimeOffset.MinValue;
        }

        // 2. If token is expired, try delegated refresh via Claude CLI
        if (credentials is not null && IsTokenExpired(credentials))
        {
            await TryDelegatedRefreshAsync(cancellationToken).ConfigureAwait(false);
            credentials = await LoadCredentialsAsync(_configDir);

            var newFingerprint = ComputeFingerprint(credentials);
            if (newFingerprint != _lastCredentialFingerprint)
            {
                _lastCredentialFingerprint = newFingerprint;
                _consecutiveFailures = 0;
                _blockedUntil = DateTimeOffset.MinValue;
            }
        }

        // 3. Check failure gate
        if (DateTimeOffset.UtcNow < _blockedUntil)
        {
            return GetCachedResult();
        }

        // 4. Attempt fresh fetch
        var fresh = await TryFetchAsync(credentials, cancellationToken).ConfigureAwait(false);
        if (fresh is not null)
        {
            _consecutiveFailures = 0;
            _blockedUntil = DateTimeOffset.MinValue;
            SetMemoryCache(fresh);
            _ = WriteDiskCacheAsync(fresh);
            return fresh;
        }

        // 5. Record failure & compute backoff
        _consecutiveFailures++;
        _blockedUntil = DateTimeOffset.UtcNow + ComputeBackoff(_consecutiveFailures);

        return GetCachedResult();
    }

    //  HTTP
    private async Task<ClaudeOAuthUsageResult?> TryFetchAsync(
        ClaudeCredentials? credentials, CancellationToken cancellationToken)
    {
        try
        {
            if (credentials?.AccessToken is null || IsTokenExpired(credentials))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, UsagePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseResponse(content, credentials.SubscriptionType, credentials.RateLimitTier);
            }

            return null;
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

    /// <summary>
    /// Runs <c>claude /status</c> which internally triggers Claude Code's
    /// OAuth token refresh.  After the command returns the credentials file
    /// is expected to contain a fresh token.
    /// </summary>
    private async Task TryDelegatedRefreshAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _refreshBlockedUntil)
        {
            return;
        }

        try
        {
            var claudePath = FindClaudeCli();
            if (claudePath is null)
            {
                _refreshBlockedUntil = DateTimeOffset.UtcNow + RefreshCooldownFailure;
                return;
            }

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = claudePath,
                Arguments = "/status",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Propagate the profile config dir so Claude Code uses the right credentials.
            if (_configDir is not null)
            {
                process.StartInfo.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = _configDir;
            }

            process.Start();

            // Wait up to 5 seconds for the process to complete
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out — kill the process but don't propagate
                try { process.Kill(); } catch { /* best effort */ }
            }

            _refreshBlockedUntil = DateTimeOffset.UtcNow + RefreshCooldownSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _refreshBlockedUntil = DateTimeOffset.UtcNow + RefreshCooldownFailure;
        }
    }

    private static string? FindClaudeCli()
    {
        // Check well-known locations
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".local", "bin", "claude"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Fall back to PATH
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = "claude",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            process.Start();
            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(2000);
            if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
            {
                return output;
            }
        }
        catch
        {
            // Ignore — CLI not available
        }

        return null;
    }

    //  Backoff
    private static TimeSpan ComputeBackoff(int failures)
    {
        var multiplier = Math.Pow(2, Math.Max(failures - 1, 0));
        var seconds = BackoffBase.TotalSeconds * multiplier;
        return seconds >= BackoffCap.TotalSeconds
            ? BackoffCap
            : TimeSpan.FromSeconds(seconds);
    }

    //  Two-tier cache (memory + disk)
    private void SetMemoryCache(ClaudeOAuthUsageResult result)
    {
        _memoryCache = result;
        _memoryCacheWrittenAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns the best available cached result: memory first, then disk.
    /// The result is validated against age and quota window freshness.
    /// </summary>
    private ClaudeOAuthUsageResult? GetCachedResult()
    {
        // Try memory cache (30 min TTL)
        if (_memoryCache is not null
            && DateTimeOffset.UtcNow - _memoryCacheWrittenAt <= MemoryCacheTtl
            && IsCacheValid(_memoryCache))
        {
            return _memoryCache;
        }

        // Try disk cache
        var fromDisk = ReadDiskCache();
        if (fromDisk is not null && IsCacheValid(fromDisk))
        {
            SetMemoryCache(fromDisk);
            return fromDisk;
        }

        return null;
    }

    private static bool IsCacheValid(ClaudeOAuthUsageResult cached)
    {
        var now = DateTimeOffset.UtcNow;

        if (now - cached.FetchedAt > MaxCacheAge)
        {
            return false;
        }

        var sessionExpired = cached.FiveHourResetsAt.HasValue && now > cached.FiveHourResetsAt.Value;
        var weekExpired = cached.SevenDayResetsAt.HasValue && now > cached.SevenDayResetsAt.Value;
        return !(sessionExpired && weekExpired);
    }

    // Disk cache I/O
    private string GetDiskCachePath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profileSuffix = _configDir is not null
            ? "_" + Path.GetFileName(_configDir)
            : "";
        return Path.Combine(basePath, "costats", "cache", $"claude-oauth{profileSuffix}.json");
    }

    private async Task WriteDiskCacheAsync(ClaudeOAuthUsageResult result)
    {
        try
        {
            var path = GetDiskCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(result, DiskCacheJsonOptions);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }
        catch
        {
            // Non-critical — memory cache still works
        }
    }

    private ClaudeOAuthUsageResult? ReadDiskCache()
    {
        try
        {
            var path = GetDiskCachePath();
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClaudeOAuthUsageResult>(json, DiskCacheJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions DiskCacheJsonOptions = new(JsonSerializerDefaults.Web);

    //  Credential helpers
    private static string? ComputeFingerprint(ClaudeCredentials? credentials)
    {
        if (credentials?.AccessToken is null)
        {
            return null;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(credentials.AccessToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8);
    }

    private static bool IsTokenExpired(ClaudeCredentials credentials)
    {
        return credentials.ExpiresAt.HasValue
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > credentials.ExpiresAt.Value;
    }

    private static async Task<ClaudeCredentials?> LoadCredentialsAsync(string? configDir)
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

    //  Response parsing
    private static ClaudeOAuthUsageResult? ParseResponse(string json, string? subscriptionType, string? rateLimitTier)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double? fiveHourPercent = null;
            DateTimeOffset? fiveHourResetsAt = null;
            double? sevenDayPercent = null;
            DateTimeOffset? sevenDayResetsAt = null;

            if (root.TryGetProperty("five_hour", out var fiveHour))
            {
                if (fiveHour.TryGetProperty("utilization", out var util))
                {
                    fiveHourPercent = util.ValueKind == JsonValueKind.Number ? util.GetDouble() : null;
                }
                if (fiveHour.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(resets.GetString(), out var resetsTime))
                    {
                        fiveHourResetsAt = resetsTime;
                    }
                }
            }

            if (root.TryGetProperty("seven_day", out var sevenDay))
            {
                if (sevenDay.TryGetProperty("utilization", out var util))
                {
                    sevenDayPercent = util.ValueKind == JsonValueKind.Number ? util.GetDouble() : null;
                }
                if (sevenDay.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(resets.GetString(), out var resetsTime))
                    {
                        sevenDayResetsAt = resetsTime;
                    }
                }
            }

            double? extraUsed = null;
            double? extraLimit = null;
            bool overageEnabled = false;
            if (root.TryGetProperty("extra_usage", out var extra))
            {
                if (extra.TryGetProperty("is_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                {
                    overageEnabled = true;
                }
                if (extra.TryGetProperty("used_credits", out var used) && used.ValueKind == JsonValueKind.Number)
                {
                    extraUsed = used.GetDouble();
                }
                if (extra.TryGetProperty("monthly_limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
                {
                    extraLimit = limit.GetDouble();
                }

                if (extraUsed.HasValue && extraLimit.HasValue)
                {
                    (extraUsed, extraLimit) = NormalizeMonetaryValues(extraUsed.Value, extraLimit.Value, subscriptionType);
                }
            }

            var scopedLimits = ParseScopedLimits(root);

            return new ClaudeOAuthUsageResult(
                fiveHourPercent,
                fiveHourResetsAt,
                sevenDayPercent,
                sevenDayResetsAt,
                overageEnabled,
                extraUsed,
                extraLimit,
                subscriptionType,
                rateLimitTier,
                DateTimeOffset.UtcNow,
                scopedLimits);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads model/surface-scoped windows from the "limits" array (e.g. the
    /// Fable-specific weekly limit). Unscoped entries duplicate five_hour /
    /// seven_day and are skipped.
    /// </summary>
    private static IReadOnlyList<costats.Core.Pulse.ScopedQuota> ParseScopedLimits(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<costats.Core.Pulse.ScopedQuota>();
        foreach (var entry in limits.EnumerateArray())
        {
            if (!entry.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? label = null;
            if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
                model.TryGetProperty("display_name", out var modelName) && modelName.ValueKind == JsonValueKind.String)
            {
                label = modelName.GetString();
            }
            else if (scope.TryGetProperty("surface", out var surface) && surface.ValueKind == JsonValueKind.String)
            {
                label = surface.GetString();
            }

            if (string.IsNullOrWhiteSpace(label) ||
                !entry.TryGetProperty("percent", out var percent) || percent.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var group = entry.TryGetProperty("group", out var groupProp) && groupProp.ValueKind == JsonValueKind.String
                ? groupProp.GetString() ?? "weekly"
                : "weekly";

            DateTimeOffset? resetsAt = null;
            if (entry.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(resets.GetString(), out var resetsTime))
            {
                resetsAt = resetsTime;
            }

            var isActive = entry.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.True;

            result.Add(new costats.Core.Pulse.ScopedQuota(
                label!,
                group,
                (long)Math.Round(Math.Clamp(percent.GetDouble(), 0, 100)),
                resetsAt,
                isActive));
        }

        return result;
    }

    private static (double used, double limit) NormalizeMonetaryValues(double rawUsed, double rawLimit, string? tier)
    {
        var usedDollars = rawUsed / 100.0;
        var limitDollars = rawLimit / 100.0;

        const double PlausibilityThreshold = 500.0;
        var isEnterpriseTier = tier?.Contains("enterprise", StringComparison.OrdinalIgnoreCase) == true;

        if (!isEnterpriseTier && limitDollars > PlausibilityThreshold)
        {
            usedDollars /= 100.0;
            limitDollars /= 100.0;
        }

        return (usedDollars, limitDollars);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record ClaudeCredentials(
        string? AccessToken,
        string? RefreshToken,
        long? ExpiresAt,
        string? SubscriptionType,
        string? RateLimitTier);
}

public sealed record ClaudeOAuthUsageResult(
    double? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayUsedPercent,
    DateTimeOffset? SevenDayResetsAt,
    bool OverageEnabled,
    double? OverageSpentUsd,
    double? OverageCeilingUsd,
    string? SubscriptionType,
    string? RateLimitTier,
    DateTimeOffset FetchedAt,
    IReadOnlyList<costats.Core.Pulse.ScopedQuota>? ScopedLimits = null);
