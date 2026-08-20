using CommunityToolkit.Mvvm.ComponentModel;
using costats.Application.Settings;

namespace costats.App.ViewModels;

/// <summary>
/// One editable row in the Settings accounts list. Edits are pushed back into
/// <see cref="AppSettings.Accounts"/> by <see cref="SettingsViewModel.SaveAccounts"/>,
/// which the owning view model wires through <see cref="Saved"/>.
/// </summary>
public sealed partial class AccountEditorViewModel : ObservableObject
{
    public AccountEditorViewModel(MonitoredAccountSettings account)
    {
        Id = account.Id;
        Type = account.Type.ToLowerInvariant();
        displayName = MonitoredAccountSettings.NormalizeDisplayName(account.DisplayName, account.Id);
        configDir = account.ConfigDir;
    }

    public string Id { get; }

    public string Type { get; }

    public string TypeLabel => Type == MonitoredAccountSettings.ClaudeType ? "Claude" : "Codex";

    public string ConfigDirLabel => Type == MonitoredAccountSettings.ClaudeType
        ? "Profile folder (CLAUDE_CONFIG_DIR)"
        : "Profile folder (CODEX_HOME)";

    [ObservableProperty]
    private string displayName;

    [ObservableProperty]
    private string configDir;

    /// <summary>Raised after any field edit so the owner can persist the list.</summary>
    public event EventHandler? Saved;

    partial void OnDisplayNameChanged(string value)
    {
        var normalized = MonitoredAccountSettings.NormalizeDisplayName(value, Id);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            displayName = normalized;
            OnPropertyChanged(nameof(DisplayName));
        }

        Saved?.Invoke(this, EventArgs.Empty);
    }

    partial void OnConfigDirChanged(string value) => Saved?.Invoke(this, EventArgs.Empty);

    public MonitoredAccountSettings ToSettings() => new()
    {
        Id = Id,
        Type = Type,
        DisplayName = DisplayName,
        ConfigDir = ConfigDir?.Trim() ?? string.Empty
    };
}
