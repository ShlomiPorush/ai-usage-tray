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
    string DisplayName)
{
    /// <summary>The file name pattern of the provider's log files under <see cref="Path"/>.</summary>
    public string FilePattern => Provider switch
    {
        UsageProviderKind.Claude => "*.jsonl",
        UsageProviderKind.Codex => "rollout-*.jsonl",
        UsageProviderKind.Antigravity => "*.db",
        _ => throw new InvalidOperationException("Unknown usage provider " + Provider)
    };

    /// <summary>The extension every log file of the provider ends with.</summary>
    public string FileExtension => Provider == UsageProviderKind.Antigravity ? ".db" : ".jsonl";
}

/// <summary>
/// What one usage scan covers: the configured Claude and Codex accounts, and
/// whether Antigravity is turned on. Built once per scan, so the scan and the
/// history mapping that follows it agree.
/// </summary>
/// <param name="Accounts">Configured Claude and Codex accounts.</param>
/// <param name="AntigravityEnabled">True when the Antigravity account is turned on in Settings.</param>
public sealed record UsageScanScope(
    IReadOnlyList<MonitoredAccountSettings> Accounts,
    bool AntigravityEnabled);

/// <summary>
/// Where Antigravity keeps its conversation databases: the agy CLI's own
/// folder, and one folder per profile that T3 Code runs agy under.
/// </summary>
/// <param name="HomeDirectory">The user profile folder the default locations hang off.</param>
public sealed record AntigravityUsageLocations(string HomeDirectory)
{
    /// <summary>The current user's locations.</summary>
    public static AntigravityUsageLocations Default { get; } =
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Every conversation folder that exists right now. T3 Code profile
    /// folders are found by listing, since their names are hashes; a folder
    /// that cannot be listed is left out.
    /// </summary>
    public IReadOnlyList<string> ConversationFolders()
    {
        var folders = new List<string>();
        if (string.IsNullOrWhiteSpace(HomeDirectory))
        {
            return folders;
        }

        folders.Add(System.IO.Path.Combine(HomeDirectory, ".gemini", "antigravity-cli", "conversations"));

        var t3Profiles = System.IO.Path.Combine(HomeDirectory, ".t3", "userdata", "providers", "antigravity");
        try
        {
            if (Directory.Exists(t3Profiles))
            {
                foreach (var profile in Directory.EnumerateDirectories(t3Profiles).Order(StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(System.IO.Path.Combine(profile, "antigravity-acp", "conversations"));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Profiles that could not be listed are simply not scanned.
        }

        return folders;
    }
}

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
/// Antigravity conversation folders are merged the same way.
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
    /// Resolves the scan roots of <paramref name="scope"/>: the account roots
    /// of <see cref="Resolve(IEnumerable{MonitoredAccountSettings}?)"/>, plus
    /// every Antigravity conversation folder when Antigravity is on. All
    /// Antigravity folders are merged into the
    /// <see cref="UsageAccounts.MergedAntigravityId"/> account.
    /// </summary>
    public static IReadOnlyList<UsageLogRoot> Resolve(UsageScanScope scope, AntigravityUsageLocations? antigravity = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var roots = Resolve(scope.Accounts).ToList();
        if (!scope.AntigravityEnabled)
        {
            return roots;
        }

        var seen = new HashSet<string>(roots.Select(root => root.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in (antigravity ?? AntigravityUsageLocations.Default).ConversationFolders())
        {
            TryAdd(
                roots,
                seen,
                folder,
                UsageProviderKind.Antigravity,
                UsageAccounts.MergedAntigravityId,
                UsageAccounts.MergedAntigravityDisplayName);
        }

        return roots;
    }

    /// <summary>
    /// The usage-history key of an account. Claude keys come from the
    /// account's config folder rather than its settings id, because settings
    /// ids are reused after an account is removed and a new account must not
    /// inherit someone else's history. Every Codex profile shares one key, as
    /// its usage is merged everywhere else too.
    /// </summary>
    public static string HistoryKeyFor(MonitoredAccountSettings account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsClaude)
        {
            return UsageHistory.CodexAccountKey;
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(account.ConfigDir));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = account.ConfigDir.Trim();
        }

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));
        return "claude:" + Convert.ToHexStringLower(hash)[..16];
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
