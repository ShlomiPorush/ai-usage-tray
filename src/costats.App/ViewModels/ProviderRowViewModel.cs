namespace costats.App.ViewModels;

/// <summary>
/// One row in the Settings providers table. <see cref="AccountId"/> is set for
/// Claude/Codex accounts and null for the singleton Z.AI / Copilot providers.
/// For Claude/Codex, <see cref="Detail"/> holds the profile folder.
/// </summary>
public sealed record ProviderRowViewModel(
    string Kind,
    string? AccountId,
    string Name,
    string Detail,
    bool IsPrimary = false,
    bool IsShownInFloatingPanel = true,
    bool CanChangeFloatingPanelSelection = true,
    bool UsageAlertsEnabled = false,
    int UsageAlertThreshold = 90,
    bool KeepSessionActive = false)
{
    /// <summary>The pulse provider id this row maps to ("claude:x", "codex:x", "zai", "copilot").</summary>
    public string ProviderId => AccountId is null ? Kind : $"{Kind}:{AccountId}";

    public string PrimaryGlyph => IsPrimary ? "★" : "☆";

    public bool IsCodex => string.Equals(Kind, "codex", StringComparison.OrdinalIgnoreCase);

    public bool CanKeepSessionActive =>
        string.Equals(Kind, "claude", StringComparison.OrdinalIgnoreCase) || IsCodex;

    public string KindLabel => Kind switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "zai" => "Z.AI",
        _ => "Copilot"
    };
}
