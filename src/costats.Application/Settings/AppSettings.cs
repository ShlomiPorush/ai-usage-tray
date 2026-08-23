using costats.Core.RemoteView;

namespace costats.Application.Settings;

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public string Hotkey { get; set; } = "Ctrl+Alt+U";
    public bool StartAtLogin { get; set; } = false;

    /// <summary>
    /// When true, an always-on-top text panel is rendered next to the system
    /// clock, showing the same tooltip text as the tray icon. Default is
    /// <c>false</c> because users who didn't ask for it can find it
    /// intrusive; flip to <c>true</c> in <c>appsettings.json</c> if you want
    /// to see all quotas without hovering.
    /// </summary>
    public bool ShowClockPanel { get; set; } = false;

    /// <summary>
    /// Whether multicc integration is enabled. Default true when multicc is detected.
    /// </summary>
    public bool MulticcEnabled { get; set; } = true;

    /// <summary>
    /// When set, only show this single profile instead of all profiles stacked.
    /// Null means "show all profiles" (stacked mode).
    /// </summary>
    public string? MulticcSelectedProfile { get; set; }

    /// <summary>
    /// Override path for multicc config directory. Null means auto-detect (~/.multicc or $MULTICC_DIR).
    /// </summary>
    public string? MulticcConfigPath { get; set; }

    /// <summary>
    /// Whether the GitHub Copilot personal usage provider is enabled.
    /// </summary>
    public bool CopilotEnabled { get; set; } = false;

    /// <summary>
    /// When true, the widget overview cards also show each window's reset
    /// countdown. Off by default to keep the overview compact.
    /// </summary>
    public bool ShowOverviewResetTimes { get; set; } = false;

    /// <summary>
    /// UI theme: "system" (follow Windows apps theme), "light" or "dark".
    /// </summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Provider id of the primary account (e.g. "claude:claude-1", "codex:codex-2",
    /// "zai", "copilot"). When set, the tray icon shows this account's status and
    /// the account is pinned to the top of the widget overview. Null keeps the
    /// default behaviour: the icon reflects the worst window across all accounts.
    /// </summary>
    public string? PrimaryAccountId { get; set; }

    /// <summary>
    /// When true, every usage refresh also uploads a small non-sensitive
    /// snapshot (provider, account nickname, plan, usage percentages and reset
    /// times) to <see cref="RemoteViewUploadUrl"/> so it can be read from a
    /// phone. Off by default: nothing leaves the machine unless asked for.
    /// </summary>
    public bool RemoteViewEnabled { get; set; } = false;

    /// <summary>
    /// The secret write id: 32 lowercase hex characters that authorise uploading
    /// and deleting this machine's snapshot. Minted the first time remote view is
    /// enabled and then kept, so the share link stays stable. It never leaves the
    /// app: the link carries the derived <see cref="RemoteViewReadId"/> instead.
    /// </summary>
    public string? RemoteViewId { get; set; }

    /// <summary>
    /// Base URL of the upload endpoint (a Cloudflare Worker), e.g.
    /// <c>https://usage-api.example.com</c>. Snapshots are PUT to
    /// <c>{url}/u/{writeId}</c>. Must be https (or http on a loopback host);
    /// anything else is ignored.
    /// </summary>
    public string? RemoteViewUploadUrl { get; set; }

    /// <summary>
    /// Base URL of the public viewer page, e.g. <c>https://usage.example.com</c>.
    /// The shareable link is <c>{url}/?id={readId}</c>. Same https rule as
    /// <see cref="RemoteViewUploadUrl"/>.
    /// </summary>
    public string? RemoteViewPageUrl { get; set; }

    /// <summary>
    /// Upload endpoint shipped with the app, read at startup from
    /// <c>appsettings.json</c> (<c>Costats:RemoteView:UploadUrl</c>). Never
    /// serialized: it is an app default, not user state, so a later release can
    /// move the service without a stale copy in the user's settings file
    /// overriding it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DefaultRemoteViewUploadUrl { get; set; }

    /// <summary>
    /// Viewer page shipped with the app, read at startup from
    /// <c>appsettings.json</c> (<c>Costats:RemoteView:PageUrl</c>). Never
    /// serialized, for the same reason as
    /// <see cref="DefaultRemoteViewUploadUrl"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DefaultRemoteViewPageUrl { get; set; }

    /// <summary>
    /// Upload endpoint actually used: a hand-edited user value wins, otherwise
    /// the built-in default, otherwise null (remote view stays inert). A value
    /// that is not https (or http on loopback) counts as absent, so a bad
    /// override cannot downgrade the connection that carries the write id.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveRemoteViewUploadUrl =>
        RemoteViewEndpoints.Normalize(RemoteViewUploadUrl)
        ?? RemoteViewEndpoints.Normalize(DefaultRemoteViewUploadUrl);

    /// <summary>
    /// Viewer page actually used, resolved like
    /// <see cref="EffectiveRemoteViewUploadUrl"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveRemoteViewPageUrl =>
        RemoteViewEndpoints.Normalize(RemoteViewPageUrl)
        ?? RemoteViewEndpoints.Normalize(DefaultRemoteViewPageUrl);

    /// <summary>
    /// The public id derived from <see cref="RemoteViewId"/>: what the share
    /// link carries, and the only id a reader ever sees. Null when no valid
    /// write id has been minted yet.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RemoteViewReadId => RemoteViewIds.TryDeriveReadId(RemoteViewId);

    /// <summary>
    /// The link to open the remote view in a browser, or null while remote view
    /// is off or not fully configured. Built in one place so Settings and the
    /// widget can never disagree about the URL.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RemoteViewShareLink
    {
        get
        {
            var page = EffectiveRemoteViewPageUrl;
            var readId = RemoteViewReadId;
            return RemoteViewEnabled && page is not null && readId is not null
                ? $"{page.TrimEnd('/')}/?id={readId}"
                : null;
        }
    }

    /// <summary>
    /// True when the build ships a complete remote-view service, so Settings can
    /// hide the endpoint boxes and remote view becomes a single checkbox. A
    /// default that fails the https rule does not count: the boxes stay visible
    /// rather than leaving the user with a silently inert feature.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRemoteViewDefaults =>
        RemoteViewEndpoints.IsAllowed(DefaultRemoteViewUploadUrl) &&
        RemoteViewEndpoints.IsAllowed(DefaultRemoteViewPageUrl);

    /// <summary>True when any Z.AI API key is configured.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasZaiKey =>
        !string.IsNullOrWhiteSpace(ZAiCodingApiKey) || !string.IsNullOrWhiteSpace(ZAiApiKey);

    /// <summary>
    /// API key for the Z.AI / GLM coding-plan quota endpoint
    /// (<c>https://api.z.ai/api/monitor/usage/quota/limit</c>). When empty, the
    /// coding-plan path is skipped. Get the key from
    /// <c>https://z.ai/manage-apikey</c>.
    /// Never serialized: the secret lives in Windows Credential Manager and is
    /// hydrated into this in-memory property by the settings store.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ZAiCodingApiKey { get; set; }

    /// <summary>
    /// Bearer token for the Z.AI standard pay-as-you-go usage endpoint
    /// (<c>https://api.z.ai/api/paas/v4/usage</c>). Used as a fallback when
    /// no coding plan is configured. Get the key from
    /// <c>https://z.ai/manage-apikey</c>.
    /// Never serialized, for the same reason as
    /// <see cref="ZAiCodingApiKey"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ZAiApiKey { get; set; }

    /// <summary>
    /// Display name for the Z.AI / GLM provider in the tray tooltip and
    /// the click panel. Default is "GLM".
    /// </summary>
    public string ZAiDisplayName { get; set; } = "GLM";

    /// <summary>
    /// Legacy single Claude profile folder. Superseded by <see cref="Accounts"/>;
    /// still read so existing settings files keep working.
    /// </summary>
    public string? ClaudeConfigDir { get; set; }

    /// <summary>
    /// Legacy Codex account list. Superseded by <see cref="Accounts"/>;
    /// still read so existing settings files keep working.
    /// </summary>
    public List<OpenAiAccountSettings>? OpenAiAccounts { get; set; }

    /// <summary>
    /// All monitored accounts (any mix of Claude and Codex, any count). Each
    /// account points at its own profile folder: CODEX_HOME for Codex accounts,
    /// CLAUDE_CONFIG_DIR for Claude accounts. Codex owns and refreshes its own
    /// credentials; this app never reads tokens for Codex accounts.
    /// </summary>
    public List<MonitoredAccountSettings>? Accounts { get; set; }

    /// <summary>
    /// Returns the accounts to monitor, migrating from the legacy
    /// <see cref="ClaudeConfigDir"/> / <see cref="OpenAiAccounts"/> shape when
    /// <see cref="Accounts"/> has not been written yet, and falling back to the
    /// standard <c>~/.claude</c> + <c>~/.codex</c> locations on a fresh install.
    /// </summary>
    public IReadOnlyList<MonitoredAccountSettings> GetEffectiveAccounts()
    {
        if (Accounts is { Count: > 0 })
        {
            return Accounts.Where(a => a.IsValid).ToList();
        }

        var migrated = new List<MonitoredAccountSettings>();

        if (!string.IsNullOrWhiteSpace(ClaudeConfigDir))
        {
            migrated.Add(new MonitoredAccountSettings
            {
                Id = "claude-1",
                Type = MonitoredAccountSettings.ClaudeType,
                DisplayName = "Claude",
                ConfigDir = ClaudeConfigDir
            });
        }

        if (OpenAiAccounts is { Count: > 0 })
        {
            migrated.AddRange(OpenAiAccounts
                .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.CodexHome))
                .Select(a => new MonitoredAccountSettings
                {
                    Id = a.Id,
                    Type = MonitoredAccountSettings.CodexType,
                    DisplayName = string.IsNullOrWhiteSpace(a.DisplayName) ? a.Id : a.DisplayName,
                    ConfigDir = a.CodexHome
                }));
        }

        if (migrated.Count > 0)
        {
            return migrated;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            new MonitoredAccountSettings
            {
                Id = "claude-1",
                Type = MonitoredAccountSettings.ClaudeType,
                DisplayName = "Claude",
                ConfigDir = Path.Combine(home, ".claude")
            },
            new MonitoredAccountSettings
            {
                Id = "codex-1",
                Type = MonitoredAccountSettings.CodexType,
                DisplayName = "Codex",
                ConfigDir = Path.Combine(home, ".codex")
            }
        ];
    }
}

/// <summary>
/// One monitored account: a provider type plus the local profile folder its
/// credentials live in.
/// </summary>
public sealed class MonitoredAccountSettings
{
    public const string ClaudeType = "claude";
    public const string CodexType = "codex";
    public const int MaximumDisplayNameLength = 24;

    public string Id { get; set; } = string.Empty;

    /// <summary>Either <see cref="ClaudeType"/> or <see cref="CodexType"/>.</summary>
    public string Type { get; set; } = CodexType;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>CODEX_HOME for Codex accounts, CLAUDE_CONFIG_DIR for Claude accounts.</summary>
    public string ConfigDir { get; set; } = string.Empty;

    public bool IsClaude => string.Equals(Type, ClaudeType, StringComparison.OrdinalIgnoreCase);
    public bool IsCodex => string.Equals(Type, CodexType, StringComparison.OrdinalIgnoreCase);

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) &&
        (IsClaude || IsCodex) &&
        !string.IsNullOrWhiteSpace(ConfigDir);

    public static string NormalizeDisplayName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= MaximumDisplayNameLength
            ? normalized
            : normalized[..MaximumDisplayNameLength];
    }
}

public sealed class OpenAiAccountSettings
{
    public const int MaximumDisplayNameLength = 24;

    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CodexHome { get; set; } = string.Empty;

    public static string NormalizeDisplayName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= MaximumDisplayNameLength
            ? normalized
            : normalized[..MaximumDisplayNameLength];
    }
}
