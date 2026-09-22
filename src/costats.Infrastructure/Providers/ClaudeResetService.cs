using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// The redeemable resets one Claude account offers right now, read fresh from
/// the usage endpoint (never from the display cache).
/// </summary>
public sealed record ClaudeResetSnapshot(
    bool RequiresSignIn,
    ResetCreditBank Bank,
    DateTimeOffset? ResetCreditExpiresAt);

public interface IClaudeResetCreditClient
{
    Task<ClaudeResetSnapshot?> FetchAsync(string configDir, CancellationToken cancellationToken);

    Task<ResetCreditOutcome> ClaimAsync(
        string configDir, string grantId, string requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit redemption of Claude usage-limit resets ("/limit-reset" grants).
/// Background monitoring never invokes this service. Mirrors
/// <see cref="CodexResetCreditService"/>: a persisted idempotency key makes an
/// uncertain answer safe to retry without minting a second redemption.
/// </summary>
public sealed class ClaudeResetService
{
    private const string FileName = "claude-reset-credit-keys.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IClaudeResetCreditClient _client;
    private readonly string _statePath;
    private readonly SemaphoreSlim _redemptionGate = new(1, 1);
    private Dictionary<string, string>? _attempts;

    public ClaudeResetService(IClaudeResetCreditClient client, string? basePath = null)
    {
        _client = client;
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : basePath;
        _statePath = Path.Combine(root, "costats", FileName);
    }

    public Task<ClaudeResetSnapshot?> LoadAsync(string configDir, CancellationToken cancellationToken) =>
        _client.FetchAsync(configDir, cancellationToken);

    public async Task<ResetCreditOutcome> RedeemAsync(
        string configDir, string grantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        if (!await _redemptionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return ResetCreditOutcome.Busy;

        try
        {
            var snapshot = await LoadAsync(configDir, cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return ResetCreditOutcome.Unavailable;
            if (snapshot.RequiresSignIn) return ResetCreditOutcome.SignInRequired;
            var bank = snapshot.Bank;
            if (!bank.IsComplete) return ResetCreditOutcome.IncompleteList;
            var grant = bank.Credits!.SingleOrDefault(candidate => candidate.Id == grantId);
            if (grant is null || !grant.CanUseAt(DateTimeOffset.UtcNow))
                return ResetCreditOutcome.CreditUnavailable;

            // The key must outlive this process: an app restart between an
            // uncertain answer and the user's retry would otherwise mint a new
            // key, and the server would treat the retry as a second redemption.
            var attempts = Load();
            var attempt = AttemptKey(configDir, grantId);
            if (!attempts.TryGetValue(attempt, out var key))
            {
                attempts[attempt] = key = Guid.NewGuid().ToString();
                Save(attempts);
            }

            var outcome = await _client.ClaimAsync(configDir, grant.Id, key, cancellationToken)
                .ConfigureAwait(false);
            // Any definite answer settles this attempt. Unlike a Codex credit,
            // a Claude grant can be redeemed again (resets_left > 1), so a
            // settled key must be discarded or the next use of the same grant
            // would replay this request. Only an uncertain answer keeps it.
            if (outcome is not ResetCreditOutcome.Unknown and not ResetCreditOutcome.Unavailable)
            {
                attempts.Remove(attempt);
                Save(attempts);
            }

            return outcome;
        }
        finally { _redemptionGate.Release(); }
    }

    private static string AttemptKey(string configDir, string grantId)
    {
        var key = ClaudeOAuthUsageFetcher.NormalizeConfigDirectoryKey(configDir);
        return key + "\n" + grantId;
    }

    private Dictionary<string, string> Load()
    {
        if (_attempts is not null)
        {
            return _attempts;
        }

        try
        {
            if (File.Exists(_statePath))
            {
                _attempts = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(_statePath), JsonOptions);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing or damaged file only costs deduplication across
            // restarts; redemption itself must still work.
        }

        return _attempts ??= new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> attempts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(attempts, JsonOptions));
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort only; the in-memory map still dedups this session.
        }
    }
}

/// <summary>
/// Talks to the Anthropic OAuth API for the reset flow: a fresh status read
/// and the claim call. Tokens stay in memory; nothing is logged or persisted.
/// </summary>
public sealed partial class ClaudeResetCreditClient : IClaudeResetCreditClient, IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com";
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(25);

    // The server enforces these shapes; refusing locally keeps a malformed id
    // out of the request path entirely.
    [GeneratedRegex("^[a-z0-9_-]{1,40}$")]
    private static partial Regex GrantIdShape();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex RequestIdShape();

    private readonly HttpClient _httpClient;

    public ClaudeResetCreditClient()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent", $"claude-cli/{ClaudeOAuthUsageFetcher.ClientVersion} (external, cli)");
        _httpClient.DefaultRequestHeaders.Add("x-app", "cli");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ClaudeResetSnapshot?> FetchAsync(string configDir, CancellationToken cancellationToken)
    {
        var credentials = await ClaudeCredentialFile.LoadAsync(configDir).ConfigureAwait(false);
        if (credentials?.AccessToken is null || ClaudeCredentialFile.IsTokenExpired(credentials))
        {
            return new ClaudeResetSnapshot(true, ResetCreditBank.Unknown, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/oauth/usage?cedar_ember=1&skip_spend=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ClaudeResetSnapshot(true, ResetCreditBank.Unknown, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            var (available, credits, expiresAt) =
                ClaudeOAuthUsageFetcher.ParseResetGrants(document.RootElement, DateTimeOffset.UtcNow);
            return new ClaudeResetSnapshot(false, new ResetCreditBank(available, credits ?? []), expiresAt);
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

    public async Task<ResetCreditOutcome> ClaimAsync(
        string configDir, string grantId, string requestId, CancellationToken cancellationToken)
    {
        if (!GrantIdShape().IsMatch(grantId) || !RequestIdShape().IsMatch(requestId))
        {
            return ResetCreditOutcome.CreditUnavailable;
        }

        var credentials = await ClaudeCredentialFile.LoadAsync(configDir).ConfigureAwait(false);
        if (credentials?.AccessToken is null || ClaudeCredentialFile.IsTokenExpired(credentials))
        {
            return ResetCreditOutcome.SignInRequired;
        }

        var organizationUuid = await FetchOrganizationUuidAsync(credentials.AccessToken, cancellationToken)
            .ConfigureAwait(false);
        if (organizationUuid is null)
        {
            // Without the organization the claim URL cannot be built; nothing
            // was sent, so the attempt is safe to retry after a refresh.
            return ResetCreditOutcome.Unavailable;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/organizations/{Uri.EscapeDataString(organizationUuid)}/reset_rate_limits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { program = "cedar_ember", grant_id = grantId, request_id = requestId }),
                Encoding.UTF8,
                "application/json");

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ClaimTimeout);
            using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return ResetCreditOutcome.SignInRequired;
                case HttpStatusCode.TooManyRequests:
                    return ResetCreditOutcome.Cooldown;
            }
            if (!response.IsSuccessStatusCode)
            {
                return ResetCreditOutcome.Unknown;
            }

            var content = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return ParseClaimResult(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Includes the claim timeout: the request may or may not have been
            // processed, so the caller keeps the idempotency key and retries.
            return ResetCreditOutcome.Unknown;
        }
    }

    /// <summary>Result values match the Claude Code client for this endpoint.</summary>
    internal static ResetCreditOutcome ParseClaimResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.String)
            {
                return ResetCreditOutcome.Unknown;
            }

            return result.GetString() switch
            {
                "reset" => ResetCreditOutcome.Reset,
                "not_limited" => ResetCreditOutcome.NothingToReset,
                "already_used" => ResetCreditOutcome.AlreadyRedeemed,
                "ineligible" => ResetCreditOutcome.CreditUnavailable,
                "cooldown" or "rate_limited" => ResetCreditOutcome.Cooldown,
                "auth_error" => ResetCreditOutcome.SignInRequired,
                _ => ResetCreditOutcome.Unknown
            };
        }
        catch (JsonException)
        {
            return ResetCreditOutcome.Unknown;
        }
    }

    private async Task<string?> FetchOrganizationUuidAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/oauth/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ClaudeOAuthProfileParser.Parse(content)?.OrganizationUuid;
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
