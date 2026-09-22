using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;

namespace costats.App.ViewModels;

/// <summary>One account's redeemable resets, read fresh from the provider.</summary>
public sealed record ResetBankSnapshot(
    bool RequiresSignIn,
    ResetCreditBank Bank,
    DateTimeOffset? ResetCreditExpiresAt);

/// <summary>
/// Provider-specific loading, redemption and result wording behind the shared
/// reset-credits screen. Each instance is bound to one account.
/// </summary>
public interface IResetCreditGateway
{
    Task<ResetBankSnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task<ResetCreditOutcome> RedeemAsync(string creditId, CancellationToken cancellationToken);

    string Describe(ResetCreditOutcome outcome);
}

/// <summary>Codex redemption through the local app-server.</summary>
public sealed class CodexResetCreditGateway(string codexHome, CodexResetCreditService service) : IResetCreditGateway
{
    public async Task<ResetBankSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await service.LoadAsync(codexHome, cancellationToken);
        return snapshot is null
            ? null
            : new ResetBankSnapshot(snapshot.RequiresSignIn, snapshot.ResetCreditBank, snapshot.ResetCreditExpiresAt);
    }

    public Task<ResetCreditOutcome> RedeemAsync(string creditId, CancellationToken cancellationToken) =>
        service.RedeemAsync(codexHome, creditId, cancellationToken);

    public string Describe(ResetCreditOutcome outcome) => outcome switch
    {
        ResetCreditOutcome.Reset => "Reset used. Check the refreshed usage in the widget.",
        ResetCreditOutcome.AlreadyRedeemed => "This reset was already used. Check the refreshed usage in the widget.",
        ResetCreditOutcome.NothingToReset => "No eligible usage window needs a reset. No reset was used.",
        ResetCreditOutcome.NoCredit => "Codex reports no resets available. Review the refreshed list.",
        ResetCreditOutcome.IncompleteList => "The reset list is incomplete. Nothing was used. Refresh to try again.",
        ResetCreditOutcome.CreditUnavailable => "The selected reset is no longer available or cannot be used here. Select another reset.",
        ResetCreditOutcome.SignInRequired => "Sign in to this account again in Settings, then refresh.",
        ResetCreditOutcome.Unsupported => "Using a selected reset requires Codex 0.154.0 or newer. Update Codex, then try again.",
        ResetCreditOutcome.Unavailable => "Could not verify the account. Nothing was used. Refresh to try again.",
        ResetCreditOutcome.Busy => "Another reset is being checked. Wait, then refresh.",
        ResetCreditOutcome.Cooldown => "Codex declined the reset for now. Try again later.",
        _ => "Codex did not confirm the result. Refresh and check usage before retrying the same reset."
    };
}

/// <summary>Claude redemption through the Anthropic OAuth API.</summary>
public sealed class ClaudeResetCreditGateway(string configDir, ClaudeResetService service) : IResetCreditGateway
{
    public async Task<ResetBankSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await service.LoadAsync(configDir, cancellationToken);
        return snapshot is null
            ? null
            : new ResetBankSnapshot(snapshot.RequiresSignIn, snapshot.Bank, snapshot.ResetCreditExpiresAt);
    }

    public Task<ResetCreditOutcome> RedeemAsync(string creditId, CancellationToken cancellationToken) =>
        service.RedeemAsync(configDir, creditId, cancellationToken);

    public string Describe(ResetCreditOutcome outcome) => outcome switch
    {
        ResetCreditOutcome.Reset => "Reset used. Check the refreshed usage in the widget.",
        ResetCreditOutcome.AlreadyRedeemed => "This reset was already used. Check the refreshed usage in the widget.",
        ResetCreditOutcome.NothingToReset => "Claude says this account is not at a limit right now, so nothing was reset.",
        ResetCreditOutcome.NoCredit => "Claude reports no resets available. Review the refreshed list.",
        ResetCreditOutcome.IncompleteList => "The reset list is incomplete. Nothing was used. Refresh to try again.",
        ResetCreditOutcome.CreditUnavailable => "The selected reset is no longer available for this account. Refresh the list.",
        ResetCreditOutcome.SignInRequired => "Sign in to this account again in Settings, then refresh.",
        ResetCreditOutcome.Cooldown => "Claude declined the reset for now. Try again in a few minutes.",
        ResetCreditOutcome.Unavailable => "Could not verify the account. Nothing was used. Refresh to try again.",
        ResetCreditOutcome.Busy => "Another reset is being checked. Wait, then refresh.",
        _ => "Claude did not confirm the result. Refresh and check usage before retrying the same reset."
    };
}

public sealed record ResetCreditRow(ResetCredit Credit)
{
    public string Title
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Credit.Title) ? "Usage limit reset" : Credit.Title;
            return Credit.UsesLeft > 1 ? $"{title} ({Credit.UsesLeft} uses left)" : title;
        }
    }

    public string Description => Credit.Description ?? string.Empty;
    public string GrantedText => Credit.GrantedAt is { } date
        ? FormattableString.Invariant($"Granted {date.ToLocalTime():MMM d, yyyy HH:mm}") : "Grant date unavailable";
    public string ExpiryText => Credit.ExpiresAt is { } date
        ? FormattableString.Invariant($"Expires {date.ToLocalTime():MMM d, yyyy HH:mm} ({ResetCreditExpiry.RemainingText(date, DateTimeOffset.UtcNow)})") : "No expiration";
    public string AvailabilityText => Credit.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" :
        Credit.ResetType is not ResetCredit.CodexType and not ResetCredit.ClaudeType
            ? "This reset type cannot be used here." : string.Empty;
}

public sealed partial class ResetCreditsViewModel(
    string accountName, IResetCreditGateway gateway, Func<Task> refreshQuota) : ObservableObject
{
    /// <summary>Codex convenience wiring, also keeping existing call sites intact.</summary>
    public ResetCreditsViewModel(
        string accountName, string codexHome, CodexResetCreditService service, Func<Task> refreshQuota)
        : this(accountName, new CodexResetCreditGateway(codexHome, service), refreshQuota)
    {
    }

    public string AccountName { get; } = accountName;
    private ResetCreditBank _bank = ResetCreditBank.Unknown;

    [ObservableProperty]
    private IReadOnlyList<ResetCreditRow> credits = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private ResetCreditRow? selectedCredit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelReviewCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool isConfirming;

    [ObservableProperty]
    private string summary = "Loading resets...";

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private string confirmationText = string.Empty;

    public bool CanInteract => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanInteract));
    partial void OnSelectedCreditChanged(ResetCreditRow? value) => IsConfirming = false;

    private bool CanRefresh() => !IsBusy;
    private bool CanReview() => !IsBusy && !IsConfirming && CanUseSelection();
    private bool CanConfirm() => !IsBusy && IsConfirming && CanUseSelection();
    private bool CanUseSelection() => _bank.IsComplete && SelectedCredit is { } selected &&
        Credits.Contains(selected) && selected.Credit.CanUseAt(DateTimeOffset.UtcNow);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsConfirming = false;
        Status = string.Empty;
        try { await LoadCoreAsync(); }
        finally { IsBusy = false; }
    }

    private async Task<bool> LoadCoreAsync()
    {
        var previousId = SelectedCredit?.Credit.Id;
        _bank = ResetCreditBank.Unknown;
        SelectedCredit = null;
        Credits = [];
        Summary = "Loading resets...";
        try
        {
            var snapshot = await gateway.LoadAsync(CancellationToken.None);
            if (snapshot is null || snapshot.RequiresSignIn)
            {
                Summary = snapshot?.RequiresSignIn == true
                    ? "Sign in to this account again in Settings, then refresh."
                    : "Could not load resets. Refresh to try again.";
                return false;
            }
            _bank = snapshot.Bank;
            Credits = (_bank.Credits ?? []).OrderBy(credit => credit.ExpiresAt ?? DateTimeOffset.MaxValue)
                .ThenBy(credit => credit.GrantedAt).ThenBy(credit => credit.Id, StringComparer.Ordinal)
                .Select(credit => new ResetCreditRow(credit)).ToArray();
            Summary = _bank.Credits is null
                ? "Reset details are unavailable. Refresh to try again."
                : !_bank.IsComplete
                ? $"{Credits.Count} of {_bank.AvailableCount} resets loaded. The list is incomplete; refresh to use a reset."
                : _bank.AvailableCount == 0 ? "No resets available in this account."
                : $"All {_bank.AvailableCount} resets loaded. Select the one you want to use.";
            var notice = ResetCreditExpiry.Notice(_bank.Credits, snapshot.ResetCreditExpiresAt, DateTimeOffset.UtcNow);
            if (notice.Length > 0) Summary += " " + notice;
            SelectedCredit = Credits.FirstOrDefault(row => row.Credit.Id == previousId);
            return true;
        }
        catch
        {
            Summary = "Could not load resets. Refresh to try again.";
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReview))]
    private void Review()
    {
        if (!CanReview()) return;
        ConfirmationText = $"Use this reset for {AccountName}?\n{SelectedCredit!.Title}\n{SelectedCredit.Description}\n{SelectedCredit.ExpiryText}\n{SelectedCredit.GrantedText}\nThis consumes one reset from this account.";
        IsConfirming = true;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void CancelReview() => IsConfirming = false;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm()) return;
        var creditId = SelectedCredit!.Credit.Id;
        IsBusy = true;
        IsConfirming = false;
        Status = "Checking the selected reset...";
        try
        {
            var outcome = await gateway.RedeemAsync(creditId, CancellationToken.None);
            SelectedCredit = null;
            await LoadCoreAsync();
            Status = gateway.Describe(outcome);
            try { await refreshQuota(); }
            catch { Status += " Could not refresh usage. Refresh the widget to check it."; }
        }
        catch
        {
            _bank = ResetCreditBank.Unknown;
            Status = "Could not confirm the result. Refresh and check usage before trying again.";
        }
        finally { IsBusy = false; }
    }
}
