using costats.Application.Settings;
using costats.Core.Analytics;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// Translates between the account ids reports use (settings ids, and the
/// merged <c>codex</c> id) and the keys the usage history is stored under.
/// </summary>
internal sealed class HistoryAccountMap
{
    private readonly Dictionary<string, string> _keyById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageAccountInfo> _accountByKey = new(StringComparer.Ordinal);

    public static HistoryAccountMap Build(IReadOnlyList<MonitoredAccountSettings>? accounts)
    {
        var map = new HistoryAccountMap();
        foreach (var account in accounts ?? [])
        {
            if (account is null || !account.IsValid)
            {
                continue;
            }

            var key = UsageLogRootResolver.HistoryKeyFor(account);
            var info = account.IsClaude
                ? new UsageAccountInfo(
                    account.Id,
                    MonitoredAccountSettings.NormalizeDisplayName(account.DisplayName, account.Id),
                    UsageProviderKind.Claude)
                : new UsageAccountInfo(
                    UsageAccounts.MergedCodexId,
                    UsageAccounts.MergedCodexDisplayName,
                    UsageProviderKind.Codex);

            map._keyById.TryAdd(info.AccountId, key);
            map._accountByKey.TryAdd(key, info);
        }

        return map;
    }

    /// <summary>History key of a report account id, or null for an id no configured account has.</summary>
    public string? KeyOf(string accountId) =>
        _keyById.TryGetValue(accountId, out var key) ? key : null;

    /// <summary>Report account id of a history key.</summary>
    public string IdOf(string key) =>
        _accountByKey.TryGetValue(key, out var account)
            ? account.AccountId
            : UsageHistory.ArchivedAccountIdPrefix + key;

    /// <summary>The current name of a configured account's history key, for saving.</summary>
    public UsageHistoryAccount? AccountFor(string key) =>
        _accountByKey.TryGetValue(key, out var account)
            ? new UsageHistoryAccount(key, account.Provider, account.DisplayName)
            : null;

    /// <summary>
    /// The account picker entry for a history key: the configured account, or
    /// the stored name marked as removed.
    /// </summary>
    public UsageAccountInfo? InfoFor(string key, IReadOnlyDictionary<string, UsageHistoryAccount> stored)
    {
        if (_accountByKey.TryGetValue(key, out var configured))
        {
            return configured;
        }

        if (!stored.TryGetValue(key, out var known))
        {
            return null;
        }

        var name = key == UsageHistory.UnknownClaudeAccountKey
            ? UsageHistory.UnknownClaudeAccountName
            : known.Name + " (removed)";
        return new UsageAccountInfo(UsageHistory.ArchivedAccountIdPrefix + key, name, known.Provider);
    }
}
