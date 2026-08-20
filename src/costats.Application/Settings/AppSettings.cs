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

    /// <summary>True when any Z.AI API key is configured.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasZaiKey =>
        !string.IsNullOrWhiteSpace(ZAiCodingApiKey) || !string.IsNullOrWhiteSpace(ZAiApiKey);

    /// <summary>
    /// Bearer token for the Z.AI / GLM coding-plan usage endpoint
    /// (<c>https://api.z.ai/api/coding/paas/v4/usage</c>). When empty, the
    /// coding-plan path is skipped. Get the key from
    /// <c>https://z.ai/manage-apikey</c>.
    /// </summary>
    public string? ZAiCodingApiKey { get; set; }

    /// <summary>
    /// Bearer token for the Z.AI standard pay-as-you-go usage endpoint
    /// (<c>https://api.z.ai/api/paas/v4/usage</c>). Used as a fallback when
    /// no coding plan is configured. Get the key from
    /// <c>https://z.ai/manage-apikey</c>.
    /// </summary>
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
