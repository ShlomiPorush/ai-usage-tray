using costats.Core.Analytics;
using costats.Core.Pulse;

namespace costats.Application.Analytics;

/// <summary>One monitored quota account and its optional local-log bucket.</summary>
public sealed record MonitoredUsageAccount(
    string ProviderId,
    string DisplayName,
    string ProviderKind,
    IReadOnlyList<string> AnalyticsAccountIds);

/// <summary>Builds the Usage selector without collapsing distinct quota accounts.</summary>
public static class UsageAccountCatalog
{
    public static IReadOnlyList<MonitoredUsageAccount> Build(
        IEnumerable<ProviderProfile> profiles,
        IReadOnlyList<UsageAccountInfo> analyticsAccounts)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(analyticsAccounts);

        var monitored = profiles
            .GroupBy(profile => profile.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var monitoredClaudeCount = monitored.Count(profile =>
            ProviderKind(profile.ProviderId).Equals(UsageAccountMap.ClaudeKind, StringComparison.OrdinalIgnoreCase));

        return monitored
            .Select(profile =>
            {
                var kind = ProviderKind(profile.ProviderId);
                var binding = UsageAccountMap.Resolve(profile.ProviderId, analyticsAccounts);
                IReadOnlyList<string> analyticsIds;

                if (kind.Equals(UsageAccountMap.ClaudeKind, StringComparison.OrdinalIgnoreCase) && monitoredClaudeCount == 1)
                {
                    // Claude Code can keep the quota credential in one profile
                    // while its desktop/CLI token logs continue under ~/.claude.
                    // With one monitored Claude account those logs unambiguously
                    // belong to it, so include every discovered Claude bucket.
                    analyticsIds = analyticsAccounts
                        .Where(account => account.Provider == UsageProviderKind.Claude)
                        .Select(account => account.AccountId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    analyticsIds = binding is null ? [] : [binding.AccountId];
                }

                return new MonitoredUsageAccount(
                    profile.ProviderId,
                    profile.DisplayName,
                    kind,
                    analyticsIds);
            })
            .OrderBy(account => ProviderRank(account.ProviderKind))
            .ThenBy(account => account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ProviderKind(string providerId)
    {
        var separator = providerId.IndexOf(':');
        return separator > 0 ? providerId[..separator] : providerId;
    }

    private static int ProviderRank(string providerKind) => providerKind switch
    {
        "claude" => 0,
        "codex" => 1,
        "zai" => 2,
        _ => 3
    };
}
