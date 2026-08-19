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
    /// Dedicated Claude subscription OAuth profile used only by AI Usage Tray.
    /// Keeping it separate prevents API-billing Claude Code credentials from
    /// overriding the Claude desktop subscription identity.
    /// </summary>
    public string ClaudeConfigDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude-ai-usage-tray");

    /// <summary>
    /// Two isolated ChatGPT/Codex subscriptions. Each account gets its own CODEX_HOME,
    /// so Codex owns and refreshes credentials without this app reading tokens.
    /// </summary>
    public List<OpenAiAccountSettings> OpenAiAccounts { get; set; } =
    [
        new()
        {
            Id = "openai-1",
            DisplayName = "OpenAI 1",
            CodexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex-openai-1")
        },
        new()
        {
            Id = "openai-2",
            DisplayName = "OpenAI 2",
            CodexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex-openai-2")
        }
    ];
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
