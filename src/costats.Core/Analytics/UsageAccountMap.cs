namespace costats.Core.Analytics;

/// <summary>
/// The analytics bucket a monitored tray account reads its cost from.
/// </summary>
/// <param name="AccountId">Id to pass to the analytics account filter.</param>
/// <param name="Provider">Which agent the bucket belongs to.</param>
/// <param name="IsMerged">
/// True when the bucket holds more than the one account that asked for it, so
/// the surface showing the numbers has to say whose they are.
/// </param>
public sealed record UsageAccountBinding(string AccountId, UsageProviderKind Provider, bool IsMerged);

/// <summary>
/// Maps a tray provider id ("claude:claude-1", "codex:openai-2", "zai") onto the
/// usage engine's account ids.
/// </summary>
/// <remarks>
/// The two id spaces line up for Claude: the tray prefixes the settings account
/// id with its provider family, and the engine uses that same settings id.
/// Codex is the exception, and the reason this class exists: every Codex profile
/// symlinks one shared sessions directory, so the engine can only offer the
/// single merged <see cref="UsageAccounts.MergedCodexId"/> bucket. A Codex
/// account therefore binds to numbers that are not only its own, which callers
/// must label with <see cref="MergedScopeNote"/>. Providers with no local token
/// log (Z.AI, Copilot) resolve to null and show nothing.
/// </remarks>
public static class UsageAccountMap
{
    /// <summary>The tray's provider family for Claude accounts.</summary>
    public const string ClaudeKind = "claude";

    /// <summary>The tray's provider family for Codex accounts.</summary>
    public const string CodexKind = "codex";

    /// <summary>
    /// Caption a surface must add when it shows a merged bucket, so a single
    /// Codex account's panel never claims the numbers are only its own.
    /// </summary>
    public const string MergedScopeNote = "all Codex accounts";

    /// <summary>
    /// Resolves <paramref name="providerId"/> against the accounts the engine
    /// actually found, or returns null when it has nothing to show: an unknown
    /// provider family, a quota-only provider, or an account whose logs were
    /// not scanned.
    /// </summary>
    /// <param name="providerId">Tray provider id, e.g. <c>claude:claude-1</c>.</param>
    /// <param name="accounts">What <c>GetAccountsAsync</c> returned.</param>
    public static UsageAccountBinding? Resolve(string? providerId, IReadOnlyList<UsageAccountInfo>? accounts)
    {
        if (string.IsNullOrWhiteSpace(providerId) || accounts is not { Count: > 0 })
        {
            return null;
        }

        var trimmed = providerId.Trim();
        var separator = trimmed.IndexOf(':');
        var kind = separator > 0 ? trimmed[..separator] : trimmed;
        var suffix = separator > 0 ? trimmed[(separator + 1)..].Trim() : string.Empty;

        if (kind.Equals(CodexKind, StringComparison.OrdinalIgnoreCase))
        {
            var merged = accounts.FirstOrDefault(account =>
                account.Provider == UsageProviderKind.Codex &&
                account.AccountId.Equals(UsageAccounts.MergedCodexId, StringComparison.OrdinalIgnoreCase));

            return merged is null
                ? null
                : new UsageAccountBinding(merged.AccountId, UsageProviderKind.Codex, IsMerged: true);
        }

        if (!kind.Equals(ClaudeKind, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var claude = accounts.Where(account => account.Provider == UsageProviderKind.Claude).ToList();

        // A bare "claude" is the legacy single-account id. It can only mean one
        // thing when exactly one Claude account was scanned; guessing between
        // several would show someone else's spend.
        var match = suffix.Length == 0
            ? (claude.Count == 1 ? claude[0] : null)
            : claude.FirstOrDefault(account => account.AccountId.Equals(suffix, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? null
            : new UsageAccountBinding(match.AccountId, UsageProviderKind.Claude, IsMerged: false);
    }
}
