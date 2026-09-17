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
public sealed class CodexResetCreditService(ICodexAppServerClient reader, ICodexResetCreditClient consumer)
{
    private readonly SemaphoreSlim _redemptionGate = new(1, 1);
    private readonly Dictionary<(string Home, string Credit), string> _attempts = new();

    public Task<CodexAppServerRateLimitSnapshot?> LoadAsync(string codexHome, CancellationToken cancellationToken) =>
        reader.FetchAsync(codexHome, refreshToken: false, cancellationToken);

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

            var home = Path.GetFullPath(codexHome);
            if (OperatingSystem.IsWindows()) home = home.ToUpperInvariant();
            var attempt = (home, creditId);
            if (!_attempts.TryGetValue(attempt, out var key))
                _attempts[attempt] = key = Guid.NewGuid().ToString();

            var outcome = await consumer.ConsumeResetCreditAsync(codexHome, credit.Id, key, cancellationToken)
                .ConfigureAwait(false);
            // A definite refusal ends the attempt. An uncertain result must retain
            // its key, including when the user closes and reopens the dialog.
            if (outcome is ResetCreditOutcome.NothingToReset or ResetCreditOutcome.NoCredit)
                _attempts.Remove(attempt);
            return outcome;
        }
        finally { _redemptionGate.Release(); }
    }
}
