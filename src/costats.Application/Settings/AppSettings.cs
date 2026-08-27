using costats.Core.RemoteView;

namespace costats.Application.Settings;

public sealed class AppSettings
{
    /// <summary>
    /// Persisted first-run state. Null in settings written before onboarding
    /// existed, which is deliberately treated as an existing completed setup.
    /// </summary>
    public string? OnboardingState { get; set; }

    /// <summary>
    /// True only when the settings file did not exist at startup. This is
    /// runtime metadata and is never written to user settings.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFirstRun { get; set; }

    /// <summary>
    /// Shows the guided window on a genuinely fresh install, or resumes a flow
    /// that was interrupted after it started. Legacy settings with a null state
    /// do not opt existing users into onboarding.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShouldShowInitialOnboarding =>
        string.Equals(OnboardingState, OnboardingStates.Started, StringComparison.OrdinalIgnoreCase) ||
        (IsFirstRun && string.IsNullOrWhiteSpace(OnboardingState));

    /// <summary>Shows the compact setup prompt inside the widget after dismissal.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShouldShowOnboardingFallback =>
        string.Equals(OnboardingState, OnboardingStates.Dismissed, StringComparison.OrdinalIgnoreCase);

    public int RefreshMinutes { get; set; } = 5;
    public bool HotkeyEnabled { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+U";
    public bool StartAtLogin { get; set; } = false;

    /// <summary>Whether update checks run automatically in the background.</summary>
    public bool AutomaticUpdateChecksEnabled { get; set; } = true;

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
    /// When true, desktop quota numbers and progress bars show the remaining
    /// share instead of the used share. Risk colours still describe the same
    /// quota state, so more capacity is green and near-exhaustion is red.
    /// </summary>
    public bool ShowRemainingPercentages { get; set; } = false;

    /// <summary>
    /// When true, compact tray text lists Weekly before Session. This is the
    /// default; disabling it restores the original Session-before-Weekly order.
    /// </summary>
    public bool ShowWeeklyBeforeSession { get; set; } = true;

    /// <summary>
    /// Shows the compact movable status panel independently of the main tray
    /// widget. The panel remains topmost until disabled or closed.
    /// </summary>
    public bool ShowFloatingStatusPanel { get; set; } = false;

    /// <summary>
    /// Reads the short-lived development setting that incorrectly applied
    /// always-on behavior to the main widget. It is omitted on the next save.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("keepWidgetOpen")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyKeepWidgetOpen
    {
        get => null;
        set
        {
            if (value == true)
            {
                ShowFloatingStatusPanel = true;
            }
        }
    }

    /// <summary>
    /// After a previously observed Claude five-hour window expires, send one
    /// minimal Haiku prompt through the official Claude Code CLI to start the
    /// next window. Off by default because this consumes subscription quota.
    /// </summary>
    public bool AutoStartClaudeFiveHourWindow { get; set; } = false;

    /// <summary>
    /// Explicitly opts in to starting an idle OpenAI/Codex five-hour window
    /// immediately, then starting each next window after expiry through the
    /// matching account's official Codex CLI.
    /// </summary>
    public bool AutoStartCodexFiveHourWindow { get; set; } = false;

    /// <summary>
    /// The Z.AI equivalent, using Claude Code with the coding-plan endpoint and
    /// GLM-4.5-Air.
    /// </summary>
    public bool AutoStartZaiFiveHourWindow { get; set; } = false;

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
    /// Base URL of the remote-view upload endpoint, e.g.
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

    /// <summary>True when the coding-plan key required for GLM session activation is configured.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasZaiCodingKey => !string.IsNullOrWhiteSpace(ZAiCodingApiKey);

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
    /// All monitored accounts (any mix of Claude and Codex, any count). Each
    /// account points at its own profile folder: CODEX_HOME for Codex accounts,
    /// CLAUDE_CONFIG_DIR for Claude accounts. Codex owns and refreshes its own
    /// credentials; this app never reads tokens for Codex accounts.
    /// </summary>
    public List<MonitoredAccountSettings>? Accounts { get; set; }

    /// <summary>
    /// Returns the configured accounts, falling back to the standard
    /// <c>~/.claude</c> and <c>~/.codex</c> locations on a fresh install.
    /// The settings store migrates the legacy account shape before this method
    /// is called.
    /// </summary>
    public IReadOnlyList<MonitoredAccountSettings> GetEffectiveAccounts()
    {
        if (Accounts is not null)
        {
            return Accounts.Where(a => a.IsValid).ToList();
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
