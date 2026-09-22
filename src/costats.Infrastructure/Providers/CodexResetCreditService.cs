using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public enum ResetCreditOutcome
{
    Reset, AlreadyRedeemed, NothingToReset, NoCredit, IncompleteList,
    CreditUnavailable, Unavailable, SignInRequired, Unsupported, Unknown, Busy
}

public interface ICodexResetCreditClient
{
    Task<ResetCreditOutcome> ConsumeResetCreditAsync(
        string codexHome, string creditId, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>Explicit redemption only. Background monitoring never invokes this service.</summary>
public sealed class CodexResetCreditService
{
    private const string FileName = "codex-reset-credit-keys.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ICodexAppServerClient _reader;
    private readonly ICodexResetCreditClient _consumer;
    private readonly string _statePath;
    private readonly SemaphoreSlim _redemptionGate = new(1, 1);
    private Dictionary<string, string>? _attempts;

    public CodexResetCreditService(
        ICodexAppServerClient reader,
        ICodexResetCreditClient consumer,
        string? basePath = null)
    {
        _reader = reader;
        _consumer = consumer;
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : basePath;
        _statePath = Path.Combine(root, "costats", FileName);
    }

    public Task<CodexAppServerRateLimitSnapshot?> LoadAsync(string codexHome, CancellationToken cancellationToken) =>
        _reader.FetchAsync(codexHome, refreshToken: false, cancellationToken);

    public async Task<ResetCreditOutcome> RedeemAsync(
        string codexHome, string creditId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(creditId);
        if (!await _redemptionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return ResetCreditOutcome.Busy;

        try
        {
            var snapshot = await LoadAsync(codexHome, cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return ResetCreditOutcome.Unavailable;
            if (snapshot.RequiresSignIn) return ResetCreditOutcome.SignInRequired;
            var bank = snapshot.ResetCreditBank;
            if (!bank.IsComplete) return ResetCreditOutcome.IncompleteList;
            var credit = bank.Credits!.SingleOrDefault(candidate => candidate.Id == creditId);
            if (credit is null || !credit.CanUseAt(DateTimeOffset.UtcNow))
                return ResetCreditOutcome.CreditUnavailable;

            // The key must outlive this process: an app restart between an
            // uncertain answer and the user's retry would otherwise mint a new
            // key, and Codex would treat the retry as a second redemption.
            var attempts = Load();
            var attempt = AttemptKey(codexHome, creditId);
            if (!attempts.TryGetValue(attempt, out var key))
            {
                attempts[attempt] = key = Guid.NewGuid().ToString();
                Save(attempts);
            }

            var outcome = await _consumer.ConsumeResetCreditAsync(codexHome, credit.Id, key, cancellationToken)
                .ConfigureAwait(false);
            // A definite refusal ends the attempt. An uncertain result must retain
            // its key, including when the user closes and reopens the dialog.
            if (outcome is ResetCreditOutcome.NothingToReset or ResetCreditOutcome.NoCredit)
            {
                attempts.Remove(attempt);
                Save(attempts);
            }

            return outcome;
        }
        finally { _redemptionGate.Release(); }
    }

    private static string AttemptKey(string codexHome, string creditId)
    {
        var home = codexHome;
        try
        {
            home = Path.GetFullPath(codexHome);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable home still needs a deterministic key of its own.
        }

        if (OperatingSystem.IsWindows()) home = home.ToUpperInvariant();
        return home + "\n" + creditId;
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
