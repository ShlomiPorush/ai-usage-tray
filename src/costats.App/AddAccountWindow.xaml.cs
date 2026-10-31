using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using costats.Application.Settings;
using costats.Infrastructure.Antigravity;

namespace costats.App;

/// <summary>
/// Modal dialog for adding or editing a monitored provider. Shows only the
/// fields the selected provider needs: profile folder for Claude/Codex, an API
/// key for Z.AI, a personal access token for Copilot.
/// </summary>
public partial class AddAccountWindow : Window
{
    private readonly bool _isEditMode;

    public AddAccountWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
        UpdateFieldsForProvider();
    }

    /// <summary>Creates the dialog in edit mode, prefilled and locked to one provider.</summary>
    public AddAccountWindow(string providerType, string name, string? configDir, int? antigravityIntervalMinutes = null) : this()
    {
        _isEditMode = true;
        if (antigravityIntervalMinutes is { } minutes)
        {
            AntigravityIntervalBox.Text = minutes.ToString(CultureInfo.InvariantCulture);
        }
        TitleText.Text = "Edit account";
        ConfirmButton.Content = "Save";

        foreach (ComboBoxItem item in ProviderBox.Items)
        {
            if ((string)item.Tag == providerType)
            {
                ProviderBox.SelectedItem = item;
                break;
            }
        }
        ProviderBox.IsEnabled = false;

        NameBox.Text = name;
        if (configDir is not null)
        {
            FolderBox.Text = configDir;
        }
        UpdateFieldsForProvider();
    }

    /// <summary>"claude", "codex", "zai", "copilot" or "antigravity".</summary>
    public string ProviderType { get; private set; } = "claude";

    /// <summary>The Antigravity check interval the user entered, or null when it is not a valid number of minutes.</summary>
    public int? AntigravityIntervalMinutes =>
        int.TryParse(AntigravityIntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
        minutes is >= AppSettings.MinAntigravityIntervalMinutes and <= AppSettings.MaxAntigravityIntervalMinutes
            ? minutes
            : null;

    public string AccountName => NameBox.Text.Trim();
    public string ConfigDir => FolderBox.Text.Trim();

    /// <summary>API key / token. In edit mode an empty value means "keep the existing secret".</summary>
    public string Secret => SecretBox.Password.Trim();

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e) => UpdateFieldsForProvider();

    private void UpdateFieldsForProvider()
    {
        if (FolderPanel is null)
        {
            return; // fired during InitializeComponent before panels exist
        }

        ProviderType = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "claude";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AntigravityPanel.Visibility = Visibility.Collapsed;
        switch (ProviderType)
        {
            case "claude":
                FolderPanel.Visibility = Visibility.Visible;
                SecretPanel.Visibility = Visibility.Collapsed;
                NamePanel.Visibility = Visibility.Visible;
                FolderLabel.Text = "Profile folder (CLAUDE_CONFIG_DIR)";
                FolderHint.Text = "The folder holding the Claude Code login. Use ~/.claude for your main login, or a dedicated folder for an extra account (sign in with: CLAUDE_CONFIG_DIR=<folder> claude).";
                if (!_isEditMode && (string.IsNullOrWhiteSpace(FolderBox.Text) || FolderBox.Text.Contains(".codex")))
                {
                    FolderBox.Text = Path.Combine(home, ".claude");
                }
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                {
                    NameBox.Text = "Claude";
                }
                break;

            case "codex":
                FolderPanel.Visibility = Visibility.Visible;
                SecretPanel.Visibility = Visibility.Collapsed;
                NamePanel.Visibility = Visibility.Visible;
                FolderLabel.Text = "Profile folder (CODEX_HOME)";
                FolderHint.Text = "The folder holding the Codex CLI login. Use ~/.codex for your main login, or a dedicated folder for an extra account (sign in with: CODEX_HOME=<folder> codex login).";
                if (!_isEditMode && (string.IsNullOrWhiteSpace(FolderBox.Text) || FolderBox.Text.Contains(".claude")))
                {
                    FolderBox.Text = Path.Combine(home, ".codex");
                }
                if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "Claude")
                {
                    NameBox.Text = "Codex";
                }
                break;

            case "zai":
                FolderPanel.Visibility = Visibility.Collapsed;
                SecretPanel.Visibility = Visibility.Visible;
                AntigravityPanel.Visibility = Visibility.Collapsed;
                NamePanel.Visibility = Visibility.Visible;
                SecretLabel.Text = "API key";
                SecretHint.Text = _isEditMode
                    ? "Coding-plan key from z.ai/manage-apikey. Leave empty to keep the current key. Stored in Windows Credential Manager."
                    : "Coding-plan key from z.ai/manage-apikey. Stored in Windows Credential Manager, not in settings.json.";
                if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text is "Claude" or "Codex")
                {
                    NameBox.Text = "GLM";
                }
                break;

            case "antigravity":
                FolderPanel.Visibility = Visibility.Collapsed;
                SecretPanel.Visibility = Visibility.Collapsed;
                NamePanel.Visibility = Visibility.Collapsed;
                AntigravityPanel.Visibility = Visibility.Visible;
                if (string.IsNullOrWhiteSpace(AntigravityIntervalBox.Text))
                {
                    AntigravityIntervalBox.Text = AppSettings.DefaultAntigravityMinIntervalMinutes.ToString(CultureInfo.InvariantCulture);
                }
                _agyFound ??= AgyExecutableResolver.Resolve() is not null;
                UpdateAntigravityStatus();
                break;

            default: // copilot
                FolderPanel.Visibility = Visibility.Collapsed;
                SecretPanel.Visibility = Visibility.Visible;
                NamePanel.Visibility = Visibility.Collapsed;
                SecretLabel.Text = "Personal access token";
                SecretHint.Text = _isEditMode
                    ? "Classic GitHub token with the copilot and read:user scopes. Leave empty to keep the current token. Stored in Windows Credential Manager."
                    : "Classic GitHub token with the copilot and read:user scopes. Stored in Windows Credential Manager, not in settings.json.";
                break;
        }
    }

    /// <summary>Whether agy was found; looked up once, the first time the Antigravity panel is shown.</summary>
    private bool? _agyFound;

    private void OnAntigravityIntervalChanged(object sender, TextChangedEventArgs e) => UpdateAntigravityStatus();

    private void UpdateAntigravityStatus()
    {
        if (AntigravityStatusText is null || _agyFound is null)
        {
            return;
        }

        if (_agyFound == false)
        {
            AntigravityStatusText.Text = "agy (Antigravity CLI) was not found. Install it from antigravity.google/cli, then sign in by running agy once.";
            return;
        }

        var minutes = AntigravityIntervalMinutes ?? AppSettings.DefaultAntigravityMinIntervalMinutes;
        AntigravityStatusText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Found agy. AI Usage Tray starts it briefly in the background on the first refresh after at least {minutes} minutes since the last check, reads the quota, and closes it.");
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the account's profile folder",
            InitialDirectory = Directory.Exists(ConfigDir)
                ? ConfigDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        string? error = ProviderType switch
        {
            "zai" when Secret.Length == 0 && !_isEditMode => "Enter the Z.AI API key.",
            "copilot" when Secret.Length == 0 && !_isEditMode => "Enter the GitHub personal access token.",
            "antigravity" when AntigravityIntervalMinutes is null => "Enter a number of minutes from 5 to 240.",
            "claude" or "codex" when ConfigDir.Length == 0 => "Choose a profile folder.",
            _ => null
        };

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
