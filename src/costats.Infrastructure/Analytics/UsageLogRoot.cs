using costats.Application.Settings;
using costats.Core.Analytics;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// One directory tree of agent logs, already attributed to a provider and an
/// account.
/// </summary>
/// <param name="Path">Fully resolved real path, symlinks followed.</param>
/// <param name="Provider">Which agent writes here.</param>
/// <param name="AccountId">Account the samples belong to.</param>
/// <param name="DisplayName">Nickname to show for that account.</param>
public sealed record UsageLogRoot(
    string Path,
    UsageProviderKind Provider,
    string AccountId,
    string DisplayName);

/// <summary>
/// Turns the configured accounts into the distinct set of directories worth
/// scanning.
/// </summary>
/// <remarks>
/// Two traps this exists to avoid:
/// <list type="number">
/// <item>
/// Codex profiles symlink their <c>sessions</c> (and <c>archived_sessions</c>)
/// folder to one shared directory. Four accounts therefore point at the same
/// files. Every candidate path is resolved to its final link target and the set
/// is deduplicated, so those files are read and counted exactly once.
/// </item>
/// <item>
/// A rollout file gives no clue which Codex profile produced it, so per-account
/// Codex attribution is impossible. All Codex roots are merged into the single
/// <see cref="UsageAccounts.MergedCodexId"/> bucket instead of being guessed at.
/// </item>
/// </list>
/// </remarks>
public static class UsageLogRootResolver
{
    /// <summary>Sub-directory of a Claude profile that holds the session logs.</summary>
    public const string ClaudeLogFolder = "projects";

    /// <summary>Sub-directories of a Codex profile that hold rollout files.</summary>
    public static readonly string[] CodexLogFolders = ["sessions", "archived_sessions"];

    /// <summary>
    /// Resolves the scan roots for <paramref name="accounts"/>, dropping
    /// directories that do not exist and collapsing paths that resolve to the
    /// same real directory. Claude roots keep their own account id; Codex roots
    /// are merged.
    /// </summary>
    public static IReadOnlyList<UsageLogRoot> Resolve(IEnumerable<MonitoredAccountSettings>? accounts)
    {
        var roots = new List<UsageLogRoot>();
        if (accounts is null)
        {
            return roots;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            if (account is null || !account.IsValid)
            {
                continue;
            }

            if (account.IsClaude)
            {
                TryAdd(
                    roots,
                    seen,
                    Path.Combine(account.ConfigDir, ClaudeLogFolder),
                    UsageProviderKind.Claude,
                    account.Id,
                    MonitoredAccountSettings.NormalizeDisplayName(account.DisplayName, account.Id));
                continue;
            }

            foreach (var folder in CodexLogFolders)
            {
                TryAdd(
                    roots,
                    seen,
                    Path.Combine(account.ConfigDir, folder),
                    UsageProviderKind.Codex,
                    UsageAccounts.MergedCodexId,
                    UsageAccounts.MergedCodexDisplayName);
            }
        }

        return roots;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to its final real directory, following a
    /// symlink or junction chain. Returns the input unchanged when it is not a
    /// link or cannot be resolved.
    /// </summary>
    public static string ResolveRealPath(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            var resolved = target?.FullName ?? info.FullName;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static void TryAdd(
        List<UsageLogRoot> roots,
        HashSet<string> seen,
        string candidate,
        UsageProviderKind provider,
        string accountId,
        string displayName)
    {
        if (!Directory.Exists(candidate))
        {
            return;
        }

        var real = ResolveRealPath(candidate);
        if (!seen.Add(real))
        {
            return;
        }

        roots.Add(new UsageLogRoot(real, provider, accountId, displayName));
    }
}
